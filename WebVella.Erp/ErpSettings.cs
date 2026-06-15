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

		// SECURITY (OWASP A05 - CWE-798/CWE-1188): exact-match denylist of every JWT signing key that has ever
		// shipped as a committed default. Each MUST be rejected at startup so a known, forgeable signing key can
		// never remain active:
		//   - "ThisIsMySecretKey"                                   : short historical fallback default
		//   - "ThisIsMySecretKey" x3 (the 51-char literal below)    : long value committed in Site/Site.Project Config.json
		private static readonly string[] KnownDefaultJwtKeys = new[]
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

			// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): fail-fast on a missing or default JWT signing key.
			// The shipped default keys are publicly known, so accepting any of them (or an empty value) would let an
			// attacker forge valid tokens. The application must refuse to start until a strong, unique key is configured.
			// Both known shipped defaults are rejected via the KnownDefaultJwtKeys denylist: the short historical
			// fallback ("ThisIsMySecretKey") AND the long value that was committed in WebVella.Erp.Site/Config.json and
			// WebVella.Erp.Site.Project/Config.json ("ThisIsMySecretKey" repeated three times). Rejecting both catches
			// existing deployments, old config files, environment variables, or secret stores still carrying a known
			// default, which would otherwise keep a forgeable signing key active.
			// Only the previous silent-default behavior is replaced here; the configuration key string "Settings:Jwt:Key"
			// is preserved unchanged so existing configuration files and secret stores continue to bind without modification.
			var jwtKey = configuration["Settings:Jwt:Key"];
			if (string.IsNullOrWhiteSpace(jwtKey) || IsKnownDefaultJwtKey(jwtKey))
			{
				throw new Exception("Settings:Jwt:Key is missing or set to a known shipped default (e.g. 'ThisIsMySecretKey' or 'ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey'). Configure a strong, unique JWT signing key (>= 32 bytes) via environment variable, user-secrets, or a secret store before starting the application.");
			}
			JwtKey = jwtKey;
			// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188): the default issuer and audience are now DISTINCT so
			// that, when neither is configured, ValidateIssuer/ValidateAudience operate on different values. The configuration
			// key strings "Settings:Jwt:Issuer"/"Settings:Jwt:Audience" are unchanged, so this remains schema-preserving.
			// Deployments that relied on the previous shared default ("webvella-erp") must set explicit, matching
			// issuer/audience values in configuration for tokens to validate across services.
			JwtIssuer = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Issuer"]) ? "webvella-erp-issuer" : configuration["Settings:Jwt:Issuer"];
			JwtAudience = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Audience"]) ? "webvella-erp-audience" : configuration["Settings:Jwt:Audience"];

			IsInitialized = true;
		}

		/// <summary>
		/// SECURITY (OWASP A05 - CWE-798/CWE-1188): returns true when <paramref name="key"/> exactly matches a known
		/// shipped default JWT signing key from <see cref="KnownDefaultJwtKeys"/>. The comparison is ordinal
		/// (case-sensitive) so it matches the exact literals that were previously committed to source/config. Such
		/// keys are publicly known and must never be accepted as a signing key.
		/// </summary>
		private static bool IsKnownDefaultJwtKey(string key)
		{
			foreach (var knownDefault in KnownDefaultJwtKeys)
			{
				if (string.Equals(key, knownDefault, StringComparison.Ordinal))
					return true;
			}
			return false;
		}
	}
}
