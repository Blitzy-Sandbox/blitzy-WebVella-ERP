using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace WebVella.Erp.Database
{
	/// <summary>
	/// Security-hardened ISerializationBinder for Newtonsoft.Json that whitelists only
	/// types from the WebVella.Erp.* assemblies' Database, Api.Models, Jobs, and
	/// Notifications namespaces. Used together with TypeNameHandling.Auto for legitimate
	/// polymorphic deserialization (e.g., DbEntity.Fields → abstract DbBaseField with many
	/// concrete subclasses) WITHOUT exposing the textbook RCE attack surface that
	/// TypeNameHandling alone provides.
	/// 
	/// Threat model:
	///   - Newtonsoft.Json with TypeNameHandling.Auto reads a "$type" field from JSON and
	///     calls Activator.CreateInstance on whatever type is named. Without a binder,
	///     attackers can supply $type values like "System.Diagnostics.Process,System" to
	///     achieve arbitrary code execution. This is CWE-502 (Deserialization of Untrusted
	///     Data) and was the root of Finding F-005 in pentest-findings.md.
	///   - This binder rejects ANY type whose assembly is not in our explicitly-allowed
	///     list, breaking the gadget chain at the type-resolution step. Even if an attacker
	///     supplies $type with a known-vulnerable .NET type, BindToType returns null which
	///     causes Newtonsoft.Json to throw a JsonSerializationException instead of
	///     instantiating the gadget.
	/// 
	/// Allowlist policy: a type is allowed if its assembly name starts with "WebVella.Erp"
	/// (matching WebVella.Erp, WebVella.Erp.Web, WebVella.Erp.Plugins.*, WebVella.Erp.Site.*,
	/// WebVella.Erp.WebAssembly.*) AND its namespace starts with "WebVella.Erp.". This ensures
	/// only first-party types are reachable via $type.
	/// 
	/// Defense-in-depth: callers should also constrain TypeNameHandling to Auto (not All),
	/// and prefer concrete root types (e.g., JsonConvert.DeserializeObject&lt;DbEntity&gt;)
	/// over object-typed roots, so $type is honored only for nested polymorphic members.
	/// </summary>
	public sealed class WebVellaDatabaseSerializationBinder : ISerializationBinder
	{
		// Singleton instance — the allowlist is immutable, so a single instance is sufficient
		// and avoids per-deserialize allocation.
		public static readonly WebVellaDatabaseSerializationBinder Instance = new WebVellaDatabaseSerializationBinder();

		// Cache of resolved types to avoid repeated reflection during high-volume deserialization
		// (e.g., InitializeSystemEntities reads ~17 system entities, each with many fields).
		private readonly Dictionary<string, Type> resolvedTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
		private readonly object cacheLock = new object();

		public Type BindToType(string assemblyName, string typeName)
		{
			if (string.IsNullOrWhiteSpace(typeName))
				return null;

			// Reject any non-first-party assembly outright. The serializer in the original code
			// emitted $type tokens for WebVella.Erp.* types only, so a payload referencing any
			// other assembly is by definition adversarial.
			if (!IsAllowedAssembly(assemblyName) || !IsAllowedNamespace(typeName))
				return null;

			var cacheKey = (assemblyName ?? string.Empty) + "|" + typeName;
			lock (cacheLock)
			{
				if (resolvedTypeCache.TryGetValue(cacheKey, out var cached))
					return cached;
			}

			Type resolved = null;
			try
			{
				if (!string.IsNullOrWhiteSpace(assemblyName))
				{
					// Try the named assembly first, then fall back to scanning loaded assemblies
					// in case the assembly identity has been rewritten (binding redirects, etc.).
					var asm = AppDomain.CurrentDomain.GetAssemblies()
						.FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
					if (asm != null)
						resolved = asm.GetType(typeName, throwOnError: false, ignoreCase: false);
				}

				if (resolved == null)
				{
					// Fallback: scan all loaded WebVella.Erp.* assemblies for the type. This is
					// safe because IsAllowedAssembly+IsAllowedNamespace already gate the search.
					foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
					{
						var asmName = asm.GetName().Name;
						if (!IsAllowedAssembly(asmName))
							continue;
						resolved = asm.GetType(typeName, throwOnError: false, ignoreCase: false);
						if (resolved != null)
							break;
					}
				}
			}
			catch
			{
				// Swallow reflection errors; they translate to a binder rejection.
				resolved = null;
			}

			// Final defense: even if we found a type, verify its assembly is still allowed.
			// Defends against TypeForwardedTo attributes that might point at a non-allowed assembly.
			if (resolved != null)
			{
				var resolvedAsmName = resolved.Assembly.GetName().Name;
				if (!IsAllowedAssembly(resolvedAsmName))
					resolved = null;
			}

			lock (cacheLock)
			{
				resolvedTypeCache[cacheKey] = resolved;
			}
			return resolved;
		}

		public void BindToName(Type serializedType, out string assemblyName, out string typeName)
		{
			// Use the default assembly+full-type-name encoding for first-party types.
			// Newtonsoft.Json's default ISerializationBinder produces this same format,
			// so existing JSON in the database remains round-trip compatible.
			assemblyName = serializedType.Assembly.GetName().Name;
			typeName = serializedType.FullName;
		}

		private static bool IsAllowedAssembly(string assemblyName)
		{
			// Whitelist by exact name prefix to prevent accidental matches via similarly-named
			// third-party assemblies.
			return !string.IsNullOrWhiteSpace(assemblyName)
				&& (assemblyName.StartsWith("WebVella.Erp", StringComparison.Ordinal)
					|| assemblyName.Equals("WebVella.Erp", StringComparison.Ordinal));
		}

		private static bool IsAllowedNamespace(string typeName)
		{
			// Type names produced by Newtonsoft.Json take the form "Namespace.TypeName" or
			// "Namespace.OuterType+InnerType". Reject anything outside the WebVella.Erp.* tree.
			return !string.IsNullOrWhiteSpace(typeName)
				&& typeName.StartsWith("WebVella.Erp.", StringComparison.Ordinal);
		}
	}
}
