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
			// QA fix: ENV-1 — Resolve cross-platform: configured Windows TZ IDs (e.g., "FLE Standard Time")
			//   are not registered on Linux's IANA-based tzdata; this normalizes the value so all
			//   downstream callers (ErpDateTimeJsonConverter, DateTimeExtensions, DbRecordRepository,
			//   RecordManager) receive an ID that resolves on the current platform.
			TimeZoneName = ResolveCrossPlatformTimeZoneId(TimeZoneName);
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

			JwtKey = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Key"]) ? "ThisIsMySecretKey" : configuration["Settings:Jwt:Key"];
			JwtIssuer = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Issuer"]) ? "webvella-erp" : configuration["Settings:Jwt:Issuer"];
			JwtAudience = string.IsNullOrWhiteSpace(configuration["Settings:Jwt:Audience"]) ? "webvella-erp" : configuration["Settings:Jwt:Audience"];

			IsInitialized = true;
		}

		// QA fix: ENV-1 — Cross-platform time-zone ID resolver. Accepts a Windows or IANA TZ ID and
		//   returns the form that resolves on the current platform's tzdata. Falls back to UTC when
		//   the configured ID cannot be resolved on this platform after Windows<->IANA conversion
		//   and known-alias substitution. This enables shipping a single Config.json across Windows
		//   and Linux deployments without per-platform overrides.
		private static string ResolveCrossPlatformTimeZoneId(string configuredId)
		{
			if (string.IsNullOrWhiteSpace(configuredId))
				return TimeZoneInfo.Utc.Id;

			// 1. The configured ID may already be valid on the current platform.
			if (TryFindTimeZone(configuredId))
				return configuredId;

			// 2. Configured ID may be a Windows ID running on Linux — try mapping to IANA.
			if (TimeZoneInfo.TryConvertWindowsIdToIanaId(configuredId, out var ianaId))
			{
				if (TryFindTimeZone(ianaId))
					return ianaId;

				// Some IANA names returned by the conversion table predate the latest tzdata
				// (e.g., "Europe/Kiev" was renamed to "Europe/Kyiv" in tzdata 2022b). Apply
				// a small alias map for the most common renames so older mappings still resolve.
				var aliased = ApplyLegacyIanaAlias(ianaId);
				if (!string.Equals(aliased, ianaId, StringComparison.Ordinal) && TryFindTimeZone(aliased))
					return aliased;
			}

			// 3. Configured ID may be an IANA ID running on Windows — try mapping to Windows.
			if (TimeZoneInfo.TryConvertIanaIdToWindowsId(configuredId, out var windowsId)
				&& TryFindTimeZone(windowsId))
				return windowsId;

			// 4. Last resort — fall back to UTC. Cannot proceed with an unresolvable TZ ID
			//    since downstream code (e.g., ErpDateTimeJsonConverter ctor) would throw.
			return TimeZoneInfo.Utc.Id;
		}

		private static bool TryFindTimeZone(string id)
		{
			try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
			catch (TimeZoneNotFoundException) { return false; }
			catch (InvalidTimeZoneException) { return false; }
		}

		private static string ApplyLegacyIanaAlias(string iana)
		{
			return iana switch
			{
				"Europe/Kiev" => "Europe/Kyiv",     // tzdata 2022b rename
				"Asia/Calcutta" => "Asia/Kolkata",   // tzdata 1993 rename
				"Asia/Saigon" => "Asia/Ho_Chi_Minh", // tzdata 1996 rename
				"Africa/Asmera" => "Africa/Asmara",  // tzdata 2008f rename
				_ => iana
			};
		}
	}
}
