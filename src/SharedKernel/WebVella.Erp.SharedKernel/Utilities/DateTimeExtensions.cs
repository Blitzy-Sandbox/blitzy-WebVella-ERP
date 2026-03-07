using WebVella.Erp.SharedKernel;

namespace System
{
	/// <summary>
	/// Extension methods for DateTime and DateTime? providing timezone conversion
	/// utilities aligned with the ERP application's configured timezone (ErpSettings.TimeZoneName).
	/// 
	/// This class is intentionally placed in the System namespace for maximum discoverability
	/// of extension methods on DateTime without requiring additional using directives.
	/// 
	/// Methods:
	///   ClearKind        — Strips DateTimeKind to Unspecified while preserving tick value
	///   ConvertToAppDate — Converts UTC/Local DateTime to the application timezone
	///   ConvertAppDateToUtc — Converts an app-timezone DateTime back to UTC
	/// </summary>
	public static class DateTimeExtensions
	{

		/// <summary>
		/// Clears the DateTimeKind of the given DateTime, returning a new DateTime
		/// with DateTimeKind.Unspecified but the same tick value.
		/// </summary>
		/// <param name="datetime">The DateTime whose Kind should be cleared.</param>
		/// <returns>A new DateTime with DateTimeKind.Unspecified.</returns>
		public static DateTime ClearKind(this DateTime datetime)
		{
			return ((DateTime?)datetime).ClearKind().Value;
		}

		/// <summary>
		/// Clears the DateTimeKind of the given nullable DateTime, returning a new DateTime?
		/// with DateTimeKind.Unspecified but the same tick value. Returns null if input is null.
		/// </summary>
		/// <param name="datetime">The nullable DateTime whose Kind should be cleared.</param>
		/// <returns>A new DateTime? with DateTimeKind.Unspecified, or null.</returns>
		public static DateTime? ClearKind(this DateTime? datetime)
		{
			if (datetime == null)
				return null;

			return new DateTime(datetime.Value.Ticks, DateTimeKind.Unspecified);
		}

		/// <summary>
		/// Converts the given DateTime from its current timezone (UTC or Local) to the
		/// application timezone configured in ErpSettings.TimeZoneName.
		/// </summary>
		/// <param name="datetime">The DateTime to convert.</param>
		/// <returns>The DateTime in the application timezone.</returns>
		public static DateTime ConvertToAppDate(this DateTime datetime)
		{
			return ((DateTime?)datetime).ConvertToAppDate().Value;
		}

		/// <summary>
		/// Converts the given nullable DateTime from its current timezone (UTC or Local) to the
		/// application timezone configured in ErpSettings.TimeZoneName.
		/// If the DateTime has DateTimeKind.Unspecified, it is assumed to already be in the
		/// application timezone and is returned as-is.
		/// </summary>
		/// <param name="datetime">The nullable DateTime to convert.</param>
		/// <returns>The DateTime in the application timezone, or null.</returns>
		public static DateTime? ConvertToAppDate(this DateTime? datetime )
        {
			if (datetime == null)
				return null;

			//If unspecified assume it is already in app TZ
			if(datetime.Value.Kind == DateTimeKind.Unspecified)
				return datetime;

			TimeZoneInfo appTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ErpSettings.TimeZoneName);
			return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(datetime.Value, appTimeZone.Id);
		}

		/// <summary>
		/// Converts the given DateTime from the application timezone to UTC.
		/// </summary>
		/// <param name="datetime">The DateTime in the application timezone.</param>
		/// <returns>The DateTime converted to UTC.</returns>
		public static DateTime ConvertAppDateToUtc(this DateTime datetime)
		{
			return ((DateTime?)datetime).ConvertAppDateToUtc().Value;
		}

		/// <summary>
		/// Converts the given nullable DateTime from the application timezone to UTC.
		/// Handles special cases:
		///   - null input returns null
		///   - UTC input is returned as-is
		///   - Local input with a different app timezone converts through the app timezone first
		///   - All other cases convert directly from app timezone to UTC
		/// </summary>
		/// <param name="appDate">The nullable DateTime in the application timezone.</param>
		/// <returns>The DateTime converted to UTC, or null.</returns>
		public static DateTime? ConvertAppDateToUtc(this DateTime? appDate)
		{
			if (appDate == null)
				return null;
			TimeZoneInfo appTimeZone = TimeZoneInfo.FindSystemTimeZoneById(ErpSettings.TimeZoneName);

			DateTime tmpDT = appDate.Value;
			if (tmpDT.Kind == DateTimeKind.Utc)
				return tmpDT;
			else if (tmpDT.Kind == DateTimeKind.Local && appTimeZone != TimeZoneInfo.Local)
			{
				var convertedToAppZoneDate = TimeZoneInfo.ConvertTime(tmpDT, appTimeZone);
				return TimeZoneInfo.ConvertTimeToUtc(convertedToAppZoneDate, appTimeZone);
			}

			return TimeZoneInfo.ConvertTimeToUtc(appDate.Value, appTimeZone);
		}
	}
}
