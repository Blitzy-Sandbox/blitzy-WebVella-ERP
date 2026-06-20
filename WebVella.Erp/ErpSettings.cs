using Microsoft.Extensions.Configuration;
using System;
using System.Net.Security;
using System.Text;

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
		// when the configured Settings:Jwt:Key matches any of these (compared case-insensitively after
		// trimming surrounding whitespace, so a different-case or stray-whitespace copy of a shipped
		// default is also rejected), so JWTs can never be signed with a guessable, public key. The set is:
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

		// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188): Minimum acceptable length, in bytes,
		// of a configured JWT signing key on a JWT-enabled host. The tokens are signed with HMAC-SHA256,
		// whose security guarantees require a key of at least 256 bits / 32 bytes, so a shorter key is
		// rejected at startup on JWT-enabled hosts (or whenever a key is supplied via an overlay).
		private const int MinimumJwtKeyByteLength = 32;

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

			// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Fail-fast validation of the
			// JWT signing key. Whether a key is REQUIRED depends on whether this host actually uses JWT bearer
			// authentication, which is determined by the presence of a Settings:Jwt configuration section: the
			// JWT-enabled hosts (WebVella.Erp.Site, WebVella.Erp.Site.Project) ship a Settings:Jwt section in
			// their Config.json, while the cookie-only hosts (WebVella.Erp.Site.Crm, .Site.Mail,
			// .Site.MicrosoftCDM, .Site.Next, .Site.Sdk) and the console app ship none.
			//   * When a Settings:Jwt section IS present (a JWT-enabled host) OR a key has been supplied through
			//     an environment-variable / user-secrets / secret-store overlay, a strong, unique signing key is
			//     MANDATORY. Startup fails fast when the key is missing / empty / whitespace, when it matches one
			//     of the shipped insecure default literals (compared case-insensitively after trimming, so a
			//     different-case or stray-whitespace copy is also rejected), or when it is shorter than the
			//     HMAC-SHA256 minimum of 32 bytes. A JWT host therefore can never start with an empty or
			//     guessable key and silently issue / validate tokens with it.
			//   * When NO Settings:Jwt section is present and no key has been supplied, JwtKey is left null so the
			//     cookie-only hosts and the console app - which do not use JWT bearer authentication - start
			//     normally. AuthService reads ErpSettings.JwtKey only at request time (never at startup), so a
			//     null key cannot crash startup on such a host.
			// The configured key value is stored UNCHANGED (it is only trimmed on a copy for the checks above) so
			// the value AuthService signs / validates with (ErpSettings.JwtKey) stays byte-for-byte identical to
			// the value each host's JwtBearer setup reads directly from Configuration["Settings:Jwt:Key"].
			var jwtKey = configuration["Settings:Jwt:Key"];
			var jwtSectionPresent = configuration.GetSection("Settings:Jwt").Exists();
			if (jwtSectionPresent || !string.IsNullOrWhiteSpace(jwtKey))
			{
				// This host uses JWT bearer authentication (a Settings:Jwt section is present) or a key was
				// supplied via an overlay: a strong, unique signing key is required. Fail fast otherwise. The
				// original value is stored unchanged (only a trimmed copy is inspected by the validator).
				ValidateJwtSigningKeyOrThrow(jwtKey);
				JwtKey = jwtKey;
			}
			else
			{
				// Cookie-only host / console app: no Settings:Jwt section and no overlaid key. JWT token
				// issuance / validation is simply unavailable here; JwtKey stays null and startup proceeds.
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

		// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Validates the JWT signing key
		// for a JWT-enabled host (or a key supplied via an overlay) and throws to fail startup fast when it
		// is unsafe. A key is rejected when it is null / empty / whitespace, when (after trimming) it matches
		// one of the shipped insecure default literals (case-insensitive), or when it is shorter than the
		// HMAC-SHA256 minimum of MinimumJwtKeyByteLength bytes. The configured value is never echoed to logs.
		// Only a trimmed COPY is inspected here; the caller stores the original value unchanged so signing
		// (AuthService) and validation (each host's JwtBearer setup) continue to use identical key bytes.
		private static void ValidateJwtSigningKeyOrThrow(string jwtKey)
		{
			if (string.IsNullOrWhiteSpace(jwtKey))
			{
				throw new Exception("Settings:Jwt:Key is not configured for a JWT-enabled host. Configure a strong, unique JWT signing key (>= 32 bytes) via environment variable, user-secrets, or a secret store before starting the application.");
			}

			var candidate = jwtKey.Trim();

			if (IsInsecureJwtKeyDefault(candidate))
			{
				throw new Exception("Settings:Jwt:Key is set to one of the shipped insecure defaults. Configure a strong, unique JWT signing key (>= 32 bytes) via environment variable, user-secrets, or a secret store before starting the application.");
			}

			if (Encoding.UTF8.GetByteCount(candidate) < MinimumJwtKeyByteLength)
			{
				throw new Exception("Settings:Jwt:Key is too short. Configure a strong, unique JWT signing key of at least 32 bytes via environment variable, user-secrets, or a secret store before starting the application.");
			}
		}

		// SECURITY (OWASP A05 Security Misconfiguration - CWE-1188/CWE-798): Returns true when the
		// supplied JWT signing key matches any known-insecure default that was previously shipped in
		// source (the short code fallback or a value committed to a host Config.json). Comparison is
		// case-insensitive (the caller has already trimmed surrounding whitespace), so a different-case or
		// stray-whitespace copy of a shipped default is also detected. The value is never echoed to logs.
		private static bool IsInsecureJwtKeyDefault(string jwtKey)
		{
			foreach (var insecureDefault in InsecureJwtKeyDefaults)
			{
				if (string.Equals(jwtKey, insecureDefault, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}
	}
}
