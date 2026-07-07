using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace WebVella.Erp.Utilities
{
	// SECURITY (A08 — Software & Data Integrity Failures / CWE-502 Deserialization of Untrusted Data).
	// Newtonsoft.Json TypeNameHandling (used by JobDataService, NotificationContext, DbEntityRepository and
	// DbRelationRepository) resolves CLR types from an embedded "$type" token, which lets a crafted payload
	// instantiate arbitrary "gadget" types and potentially achieve remote code execution. This binder is the
	// mitigation recommended by the Microsoft analyzers CA2326-CA2330: an ALLOWLIST that resolves only
	// first-party WebVella types (any assembly whose simple name starts with "WebVella", which covers the core
	// library and all WebVella.Erp.Plugins.* plugins, including plugin-defined job Attributes/Result types)
	// plus a small curated set of safe BCL container/primitive types, and REJECTS (throws) everything else so
	// a forbidden type is never instantiated (fail-closed). Attach it via
	// JsonSerializerSettings.SerializationBinder at each TypeNameHandling site.
	public class ErpSerializationBinder : ISerializationBinder
	{
		// Shared, thread-safe instance (this binder is stateless / immutable).
		public static readonly ErpSerializationBinder Instance = new ErpSerializationBinder();

		// Newtonsoft's default binder performs the actual name<->type resolution and is internally cached / thread-safe.
		private static readonly DefaultSerializationBinder DefaultBinder = new DefaultSerializationBinder();

		// Curated set of safe BCL types Newtonsoft may legitimately emit inside WebVella object graphs:
		// generic containers (as OPEN generic definitions), primitives and common value types. None of these
		// are exploitable deserialization gadgets.
		private static readonly HashSet<Type> AllowedBclTypes = new HashSet<Type>
		{
			typeof(object),
			typeof(string),
			typeof(bool),
			typeof(byte),
			typeof(sbyte),
			typeof(char),
			typeof(short),
			typeof(ushort),
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(float),
			typeof(double),
			typeof(decimal),
			typeof(Guid),
			typeof(DateTime),
			typeof(DateTimeOffset),
			typeof(TimeSpan),
			typeof(byte[]),
			// Open generic definitions (matched via Type.GetGenericTypeDefinition()):
			typeof(List<>),
			typeof(Dictionary<,>),
			typeof(HashSet<>),
			typeof(Nullable<>)
		};

		public ErpSerializationBinder()
		{
		}

		public void BindToName(Type serializedType, out string assemblyName, out string typeName)
		{
			// Delegate serialization naming to the default binder so serialized output is byte-for-byte
			// unchanged, preserving round-trip compatibility with data already persisted in the database.
			DefaultBinder.BindToName(serializedType, out assemblyName, out typeName);
		}

		public Type BindToType(string assemblyName, string typeName)
		{
			// Resolve type metadata via the default binder. IMPORTANT: resolving a Type does NOT construct an
			// instance or execute user code - Newtonsoft only instantiates the type AFTER this method returns.
			// By THROWING (never returning) for a disallowed type, a gadget is never instantiated (fail-closed).
			Type resolvedType;
			try
			{
				resolvedType = DefaultBinder.BindToType(assemblyName, typeName);
			}
			catch (JsonSerializationException)
			{
				// Unresolved / forbidden (e.g. a gadget assembly that is not referenced by the application).
				throw new JsonSerializationException(
					$"ErpSerializationBinder blocked deserialization of unresolved or forbidden type '{typeName}' from assembly '{assemblyName}' (A08 / CWE-502).");
			}

			if (resolvedType == null || !IsTypeAllowed(resolvedType))
			{
				throw new JsonSerializationException(
					$"ErpSerializationBinder blocked deserialization of forbidden type '{typeName}' from assembly '{assemblyName}' (A08 / CWE-502).");
			}

			return resolvedType;
		}

		// Allow first-party (any assembly whose simple name starts with "WebVella") plus the curated safe BCL
		// set. Recurse through array element types and generic arguments so a safe container cannot smuggle a
		// forbidden element type (e.g. List<SomeGadget>).
		private static bool IsTypeAllowed(Type type)
		{
			if (type == null)
				return false;

			// Arrays: validate the element type.
			if (type.IsArray)
				return IsTypeAllowed(type.GetElementType());

			// First-party WebVella core + plugin types (all such assemblies are named "WebVella.*").
			string assemblySimpleName = type.Assembly.GetName().Name;
			if (!string.IsNullOrEmpty(assemblySimpleName) &&
				assemblySimpleName.StartsWith("WebVella", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			// Constructed generics: the OPEN definition must be safe AND every type argument must be allowed.
			if (type.IsGenericType)
			{
				Type definition = type.GetGenericTypeDefinition();
				if (!AllowedBclTypes.Contains(definition))
					return false;

				foreach (Type argument in type.GetGenericArguments())
				{
					if (!IsTypeAllowed(argument))
						return false;
				}

				return true;
			}

			// Non-generic BCL types must be explicitly on the curated safe list.
			return AllowedBclTypes.Contains(type);
		}
	}
}
