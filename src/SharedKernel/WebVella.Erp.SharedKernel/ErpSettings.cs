using Microsoft.Extensions.Configuration;
using System;

namespace WebVella.Erp.SharedKernel
{
	/// <summary>
	/// Shared static configuration binder for SharedKernel utilities.
	/// Contains only cross-cutting properties needed by SharedKernel classes.
	/// Service-specific settings (ConnectionString, Email*, JWT*, etc.) are
	/// handled by per-service appsettings.json.
	/// </summary>
	public static class ErpSettings
	{
		/// <summary>
		/// Encryption key used by CryptoUtility for symmetric encryption.
		/// Bound from Settings:EncryptionKey (or backward-compatible Settings:EncriptionKey).
		/// </summary>
		public static string EncryptionKey { get; private set; }

		/// <summary>
		/// Language code for the ERP instance (default: "en").
		/// </summary>
		public static string Lang { get; private set; }

		/// <summary>
		/// Locale string for the ERP instance (default: "en-US").
		/// </summary>
		public static string Locale { get; private set; }

		/// <summary>
		/// Cache key used for cache busting (default: current date YYYYMMDD).
		/// </summary>
		public static string CacheKey { get; private set; }

		/// <summary>
		/// IANA or Windows timezone name for ERP datetime operations
		/// (default: "FLE Standard Time").
		/// </summary>
		public static string TimeZoneName { get; set; }

		/// <summary>
		/// DateTime format string used by JSON serialization
		/// (default: "yyyy-MM-ddTHH:mm:ss.fff").
		/// </summary>
		public static string JsonDateTimeFormat { get; set; }

		/// <summary>
		/// Whether the application is running in development/debug mode.
		/// </summary>
		public static bool DevelopmentMode { get; private set; }

		/// <summary>
		/// Display name of the application.
		/// </summary>
		public static string AppName { get; private set; }

		/// <summary>
		/// Root IConfiguration instance for nested access.
		/// </summary>
		public static IConfiguration Configuration { get; private set; }

		/// <summary>
		/// Guard flag indicating whether Initialize() has been called.
		/// </summary>
		public static bool IsInitialized { get; private set; }

		/// <summary>
		/// Initializes the shared settings from the provided configuration.
		/// Must be called once during application startup.
		/// </summary>
		/// <param name="configuration">The application configuration root.</param>
		public static void Initialize(IConfiguration configuration)
		{
			Configuration = configuration;

			// EncryptionKey — with backward compatibility for misspelled key
			var encryptionKey = configuration["Settings:EncryptionKey"];
			if (string.IsNullOrWhiteSpace(encryptionKey))
				encryptionKey = configuration["Settings:EncriptionKey"];
			EncryptionKey = encryptionKey;

			// Lang
			var lang = configuration["Settings:Lang"];
			Lang = string.IsNullOrWhiteSpace(lang) ? "en" : lang;

			// Locale
			var locale = configuration["Settings:Locale"];
			Locale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;

			// TimeZoneName
			var timeZoneName = configuration["Settings:TimeZoneName"];
			TimeZoneName = string.IsNullOrWhiteSpace(timeZoneName) ? "FLE Standard Time" : timeZoneName;

			// JsonDateTimeFormat
			var jsonDateTimeFormat = configuration["Settings:JsonDateTimeFormat"];
			JsonDateTimeFormat = string.IsNullOrWhiteSpace(jsonDateTimeFormat)
				? "yyyy-MM-ddTHH:mm:ss.fff"
				: jsonDateTimeFormat;

			// CacheKey
			var cacheKey = configuration["Settings:CacheKey"];
			CacheKey = string.IsNullOrWhiteSpace(cacheKey)
				? DateTime.Now.ToString("yyyyMMdd")
				: cacheKey;

			// DevelopmentMode
			var devMode = configuration["Settings:DevelopmentMode"];
			DevelopmentMode = !string.IsNullOrWhiteSpace(devMode) &&
				devMode.Equals("true", StringComparison.OrdinalIgnoreCase);

			// AppName
			AppName = configuration["Settings:AppName"];

			IsInitialized = true;
		}
	}
}
