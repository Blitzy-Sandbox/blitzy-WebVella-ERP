using Newtonsoft.Json.Converters;
using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel
{
	/// <summary>
	/// Custom Newtonsoft.Json DateTime converter that applies ERP timezone rules.
	/// On read (deserialization): converts incoming DateTime values to UTC based on their Kind.
	///   - UTC values pass through unchanged.
	///   - Local values are converted to UTC via ToUniversalTime().
	///   - Unspecified values are treated as being in the ERP timezone and converted to UTC.
	/// On write (serialization): converts DateTime values to the ERP local timezone for output.
	///   - UTC and Local values are converted to the ERP timezone, Kind is set to Unspecified.
	///   - Unspecified values are written as-is.
	/// The ERP timezone is configured via ErpSettings.TimeZoneName (default: "FLE Standard Time").
	/// The output format is controlled by ErpSettings.JsonDateTimeFormat (default: "yyyy-MM-ddTHH:mm:ss.fff").
	/// Used by all microservice Program.cs files in AddNewtonsoftJson() and JsonConvert.DefaultSettings.
	/// </summary>
	public class ErpDateTimeJsonConverter : DateTimeConverterBase
	{
		private static TimeZoneInfo erpTimeZone;

		public ErpDateTimeJsonConverter()
		{
			erpTimeZone = FindTimeZoneCrossPlatform(ErpSettings.TimeZoneName);
		}

		/// <summary>
		/// Cross-platform timezone resolution. Windows uses names like "FLE Standard Time"
		/// while Linux/macOS use IANA identifiers like "Europe/Kyiv". This method tries
		/// the configured name first, then falls back to a well-known mapping for the
		/// default "FLE Standard Time" timezone, and finally falls back to UTC.
		/// Fixes TimeZoneNotFoundException on Linux where "FLE Standard Time" is unavailable.
		/// </summary>
		private static TimeZoneInfo FindTimeZoneCrossPlatform(string timeZoneName)
		{
			// Try the configured timezone name directly (works on the native OS)
			if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneName, out var tz))
				return tz;

			// Map well-known Windows timezone IDs to IANA equivalents for Linux
			var windowsToIana = new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
			{
				["FLE Standard Time"] = new[] { "Europe/Kyiv", "Europe/Kiev", "Europe/Helsinki" },
				["Eastern Standard Time"] = new[] { "America/New_York" },
				["Pacific Standard Time"] = new[] { "America/Los_Angeles" },
				["Central Standard Time"] = new[] { "America/Chicago" },
				["UTC"] = new[] { "Etc/UTC" },
			};

			if (windowsToIana.TryGetValue(timeZoneName, out var ianaNames))
			{
				foreach (var ianaName in ianaNames)
				{
					if (TimeZoneInfo.TryFindSystemTimeZoneById(ianaName, out var ianaTz))
						return ianaTz;
				}
			}

			// Also try the reverse: if an IANA name was given, try it directly
			// (already handled by the first TryFind above)

			// Final fallback: UTC to prevent crashes
			return TimeZoneInfo.Utc;
		}

		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			var value = reader.Value;
			if (value == null)
				return null;
			DateTime dateTime = DateTime.Parse(value.ToString());
			switch (dateTime.Kind)
			{
				case DateTimeKind.Utc:
					return dateTime;
				case DateTimeKind.Local:
					return dateTime.ToUniversalTime();
				case DateTimeKind.Unspecified:
					return TimeZoneInfo.ConvertTimeToUtc(dateTime, erpTimeZone);
			}

			throw new Exception("ErpDateTimeJsonConverter: DateTimeKind type of parsed json date cannot be handled.");
		}

		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
		{
			switch (((DateTime)value).Kind)
			{
				case DateTimeKind.Utc:
				case DateTimeKind.Local:
					{
						DateTime erpLocalDateTime = TimeZoneInfo.ConvertTimeBySystemTimeZoneId((DateTime)value, ErpSettings.TimeZoneName);
						erpLocalDateTime = DateTime.SpecifyKind(erpLocalDateTime, DateTimeKind.Unspecified);
						writer.WriteValue(erpLocalDateTime.ToString(ErpSettings.JsonDateTimeFormat));
					}
					break;
				case DateTimeKind.Unspecified:
					{
						writer.WriteValue(((DateTime)value).ToString(ErpSettings.JsonDateTimeFormat));
					}
					break;
			}
		}
	}

}
