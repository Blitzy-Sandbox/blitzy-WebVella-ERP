using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Eql
{
	/// <summary>
	/// Abstraction for extracting typed field values from JSON query results.
	/// <para>
	/// In the monolith, <c>EqlCommand.ConvertJObjectToEntityRecord</c> called
	/// <c>DbRecordRepository.ExtractFieldValue(jObj[fieldName], field)</c> to convert
	/// JSON tokens to strongly typed CLR values based on the field type definition.
	/// In the microservice architecture, each service provides its own implementation
	/// since <c>DbRecordRepository</c> is a service-level construct, not a shared library type.
	/// </para>
	/// </summary>
	public interface IFieldValueExtractor
	{
		/// <summary>
		/// Extracts a typed value from a JSON token based on the field definition.
		/// Corresponds to the monolith's <c>DbRecordRepository.ExtractFieldValue(JToken token, Field field)</c>.
		/// Must handle all 21+ field types (text, number, date, datetime, guid, boolean, multiselect, etc.).
		/// </summary>
		/// <param name="token">The JSON token containing the raw field value from a PostgreSQL query result.</param>
		/// <param name="field">The field definition specifying the expected type and format.</param>
		/// <returns>The extracted CLR value, or null if the token is null/empty.</returns>
		object ExtractFieldValue(JToken token, Field field);
	}
}
