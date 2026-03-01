using System;
using Newtonsoft.Json.Linq;

namespace Newtonsoft.Json.Linq
{
	public static class JsonNetExtensions
	{
		/// <summary>
		/// Determines whether a JToken is null or represents an empty value.
		/// Checks for: C# null, empty JSON array, empty JSON object, empty string, or JSON null literal.
		/// Note: Whitespace-only strings are NOT considered empty — this is intentional.
		/// </summary>
		/// <param name="token">The JToken to check.</param>
		/// <returns>True if the token is null or empty; otherwise false.</returns>
		public static bool IsNullOrEmpty( this JToken token)
		{
			return (token == null) ||
			       (token.Type == JTokenType.Array && !token.HasValues) ||
			       (token.Type == JTokenType.Object && !token.HasValues) ||
			       (token.Type == JTokenType.String && token.ToString() == String.Empty) ||
			       (token.Type == JTokenType.Null);
		}
	}
}
