using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace WebVella.Erp.Utilities
{
	/// <summary>
	/// Allowlist <see cref="ISerializationBinder"/> that neutralizes the Newtonsoft.Json $type gadget
	/// remote-code-execution vector (OWASP A08, CWE-502) while preserving the existing on-wire format.
	/// On read (<see cref="BindToType"/>) only WebVella.Erp types, enums, a curated safe BCL set, arrays,
	/// and generic containers of allowed element/argument types are permitted; anything else is rejected.
	/// On write (<see cref="BindToName"/>) the default binder is used so serialized payloads remain
	/// byte-for-byte compatible (the fix is additive — existing TypeNameHandling values stay unchanged).
	/// </summary>
	public class ErpSerializationBinder : ISerializationBinder
	{
		/// <summary>
		/// Shared instance reused across the deserialization settings sites (Jobs, Notifications, Database).
		/// </summary>
		public static readonly ErpSerializationBinder Instance = new ErpSerializationBinder();

		private static readonly DefaultSerializationBinder DefaultBinder = new DefaultSerializationBinder();

		private const string AllowedNamespaceRoot = "WebVella.Erp";

		// Curated safe scalar/value/container-leaf types permitted to resolve via $type.
		private static readonly HashSet<Type> AllowedTypes = new HashSet<Type>
		{
			typeof(object),
			typeof(string),
			typeof(bool),
			typeof(byte), typeof(sbyte),
			typeof(short), typeof(ushort),
			typeof(int), typeof(uint),
			typeof(long), typeof(ulong),
			typeof(float), typeof(double), typeof(decimal),
			typeof(char),
			typeof(Guid),
			typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
			typeof(byte[]),
			// System.Dynamic.ExpandoObject is the property-bag WebVella's job infrastructure persists for
			// Job.Attributes / Job.Result / SchedulePlan.JobAttributes (serialized with TypeNameHandling.All).
			// It is a benign IDictionary<string, object> with no dangerous construction/setter side effects, so it
			// cannot act as a deserialization gadget. Allowlisting it lets legitimately-persisted job payloads
			// round-trip on the reader side (JobProfile) while genuine gadget types stay rejected (OWASP A08, CWE-502).
			typeof(System.Dynamic.ExpandoObject)
		};

		// Safe generic collection definitions whose type arguments are validated recursively.
		private static readonly HashSet<Type> AllowedGenericDefinitions = new HashSet<Type>
		{
			typeof(List<>),
			typeof(IList<>),
			typeof(ICollection<>),
			typeof(IEnumerable<>),
			typeof(Dictionary<,>),
			typeof(IDictionary<,>),
			typeof(HashSet<>),
			typeof(KeyValuePair<,>),
			typeof(Nullable<>)
		};

		/// <summary>
		/// Public parameterless constructor (allows DI registration in the Web layer in addition to the
		/// shared <see cref="Instance"/>).
		/// </summary>
		public ErpSerializationBinder()
		{
		}

		/// <inheritdoc />
		public Type BindToType(string assemblyName, string typeName)
		{
			// Resolve through the default binder first (handles assembly-qualified and generic type names),
			// then enforce the allowlist. Reject anything that is not explicitly permitted.
			Type resolvedType = DefaultBinder.BindToType(assemblyName, typeName);

			if (IsAllowed(resolvedType))
				return resolvedType;

			throw new JsonSerializationException(
				"Type '" + typeName + "' from assembly '" + assemblyName +
				"' is not allowed for deserialization by ErpSerializationBinder.");
		}

		/// <inheritdoc />
		public void BindToName(Type serializedType, out string assemblyName, out string typeName)
		{
			// Delegate to the default binder so the emitted $type format is byte-for-byte preserved.
			DefaultBinder.BindToName(serializedType, out assemblyName, out typeName);
		}

		private static bool IsAllowed(Type type)
		{
			if (type == null)
				return false;

			// Arrays: validate the element type.
			if (type.IsArray)
				return IsAllowed(type.GetElementType());

			// Generic types: definition must be a safe container (or WebVella) and every argument allowed.
			if (type.IsGenericType)
			{
				Type definition = type.GetGenericTypeDefinition();
				bool definitionAllowed = AllowedGenericDefinitions.Contains(definition) || IsWebVellaType(definition);
				if (!definitionAllowed)
					return false;

				foreach (Type argument in type.GetGenericArguments())
				{
					if (!IsAllowed(argument))
						return false;
				}

				return true;
			}

			// Enums are harmless value types (cannot be gadgets).
			if (type.IsEnum)
				return true;

			// WebVella payload types (including plugins: WebVella.Erp.Plugins.*).
			if (IsWebVellaType(type))
				return true;

			// Curated safe BCL scalar/value types.
			if (AllowedTypes.Contains(type))
				return true;

			return false;
		}

		private static bool IsWebVellaType(Type type)
		{
			return type != null
				&& type.Namespace != null
				&& (type.Namespace.Equals(AllowedNamespaceRoot, StringComparison.Ordinal)
					|| type.Namespace.StartsWith(AllowedNamespaceRoot + ".", StringComparison.Ordinal));
		}
	}
}
