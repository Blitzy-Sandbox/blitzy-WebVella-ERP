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
			erpTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ErpSettings.TimeZoneName);
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
