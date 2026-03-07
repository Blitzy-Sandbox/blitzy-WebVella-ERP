using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace WebVella.Erp.SharedKernel.Utilities
{
	/// <summary>
	/// Abstract generic JsonConverter that enables polymorphic deserialization by inspecting the
	/// raw JObject token tree before instantiating the concrete type. Subclasses override the
	/// abstract Create method to decide which concrete T to instantiate based on the JSON shape.
	/// The ReadJson method uses a JSON replay pattern: it loads the JObject, creates the target
	/// via Create(), serializes the JObject back to a string, then populates the target from a
	/// fresh JsonTextReader with propagated reader settings (Culture, DateParseHandling,
	/// DateTimeZoneHandling, FloatParseHandling) to ensure faithful deserialization.
	/// </summary>
	/// <typeparam name="T">The base type or interface for polymorphic deserialization.</typeparam>
	public abstract class JsonCreationConverter<T> : JsonConverter
	{
		/// <summary>
		/// Factory method that subclasses implement to inspect the JObject and return
		/// the appropriate concrete instance of T.
		/// </summary>
		/// <param name="objectType">The declared target type from the deserialization context.</param>
		/// <param name="jObject">The parsed JSON object to inspect for type discrimination.</param>
		/// <returns>A new instance of the concrete type to populate.</returns>
		protected abstract T Create(Type objectType, JObject jObject);

		/// <summary>
		/// Determines whether this converter can handle the specified type.
		/// Returns true if the objectType is assignable to T.
		/// </summary>
		/// <param name="objectType">The type to check for conversion eligibility.</param>
		/// <returns>True if objectType is T or derives from T; otherwise false.</returns>
		public override bool CanConvert(Type objectType)
		{
			return typeof(T).IsAssignableFrom(objectType);
		}

		/// <summary>
		/// Reads JSON and deserializes it into a polymorphic instance of T using the
		/// JSON replay pattern. The method loads the raw JSON into a JObject, calls
		/// Create() to obtain the concrete target, then serializes the JObject back to
		/// a string and creates a new JsonTextReader with propagated settings from the
		/// original reader before populating the target.
		/// </summary>
		/// <param name="reader">The JsonReader to read from.</param>
		/// <param name="objectType">The declared type of the object being deserialized.</param>
		/// <param name="existingValue">The existing value of the object being read (unused).</param>
		/// <param name="serializer">The calling serializer.</param>
		/// <returns>The deserialized object, or null if the token is null.</returns>
		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			if (reader.TokenType == JsonToken.Null)
				return null;

			// Load JObject from stream
			JObject jObject = JObject.Load(reader);

			// Create target object based on JObject
			T target = Create(objectType, jObject);

			// Populate the object properties by replaying the JSON through a new reader
			// with the original reader's settings propagated for consistent parsing behavior
			StringWriter writer = new StringWriter();
			serializer.Serialize(writer, jObject);
			using (JsonTextReader newReader = new JsonTextReader(new StringReader(writer.ToString())))
			{
				newReader.Culture = reader.Culture;
				newReader.DateParseHandling = reader.DateParseHandling;
				newReader.DateTimeZoneHandling = reader.DateTimeZoneHandling;
				newReader.FloatParseHandling = reader.FloatParseHandling;
				serializer.Populate(newReader, target);
			}

			return target;
		}

		/// <summary>
		/// Writes the JSON representation of the object. Delegates directly to the
		/// serializer's Serialize method for standard serialization behavior.
		/// </summary>
		/// <param name="writer">The JsonWriter to write to.</param>
		/// <param name="value">The object value to serialize.</param>
		/// <param name="serializer">The calling serializer.</param>
		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
		{
			serializer.Serialize(writer, value);
		}
	}
}
