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
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
		public static void Initialize(IConfiguration configuration)
		{
			if (configuration == null)
				throw new ArgumentNullException(nameof(configuration));

			Configuration = configuration;

			// EncryptionKey — with backward compatibility for misspelled "EncriptionKey" in legacy config files
			// (see original monolith comment: "628426@gmail.com 27 Jul 2020 backwards compatibility")
			EncryptionKey = configuration["Settings:EncryptionKey"];
			if (string.IsNullOrWhiteSpace(EncryptionKey))
			{
				EncryptionKey = configuration["Settings:EncriptionKey"];
			}

			// Lang — default "en"
			Lang = string.IsNullOrWhiteSpace(configuration["Settings:Lang"])
				? "en"
				: configuration["Settings:Lang"];

			// Locale — default "en-US"
			Locale = string.IsNullOrWhiteSpace(configuration["Settings:Locale"])
				? "en-US"
				: configuration["Settings:Locale"];

			// TimeZoneName — default "FLE Standard Time"
			// (GMT+02:00) Helsinki, Kiev, Riga, Sofia, Tallinn, Vilnius
			TimeZoneName = string.IsNullOrWhiteSpace(configuration["Settings:TimeZoneName"])
				? "FLE Standard Time"
				: configuration["Settings:TimeZoneName"];

			// JsonDateTimeFormat — default ISO 8601 with milliseconds
			JsonDateTimeFormat = string.IsNullOrWhiteSpace(configuration["Settings:JsonDateTimeFormat"])
				? "yyyy-MM-ddTHH:mm:ss.fff"
				: configuration["Settings:JsonDateTimeFormat"];

			// CacheKey — default to current date for daily cache busting
			CacheKey = string.IsNullOrWhiteSpace(configuration["Settings:CacheKey"])
				? DateTime.Now.ToString("yyyyMMdd")
				: configuration["Settings:CacheKey"];

			// DevelopmentMode — preserves bool.Parse behavior from monolith
			DevelopmentMode = string.IsNullOrWhiteSpace(configuration["Settings:DevelopmentMode"])
				? false
				: bool.Parse(configuration["Settings:DevelopmentMode"]);

			// AppName — no default, may be null
			AppName = configuration["Settings:AppName"];

			IsInitialized = true;
		}
	}
}
