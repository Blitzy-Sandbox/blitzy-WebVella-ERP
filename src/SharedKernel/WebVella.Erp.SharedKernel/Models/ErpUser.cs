using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Cross-service identity model representing an authenticated ERP user.
	/// Contains user profile properties and role membership used for
	/// authentication and authorization across all microservices.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.ErpUser</c>
	/// with the following cross-service adaptations:
	///   - Password is [JsonIgnore] and excluded from cross-service propagation
	///   - Enabled and Verified are [JsonIgnore] — verified at authentication time
	///   - Roles are [JsonIgnore] — propagated via JWT claims instead
	///   - IsAdmin computed property checks against SystemIds.AdministratorRoleId
	///
	/// JWT claim mapping (AAP 0.8.3):
	///   - ClaimTypes.NameIdentifier → Id
	///   - ClaimTypes.Name → Username
	///   - ClaimTypes.Email → Email
	///   - ClaimTypes.Role → Roles[].Name (one claim per role)
	/// </summary>
	[Serializable]
	public class ErpUser
	{
		public ErpUser()
		{
			Id = Guid.Empty;
			Email = String.Empty;
			Password = String.Empty;
			FirstName = String.Empty;
			LastName = String.Empty;
			Username = String.Empty;
			Enabled = true;
			Verified = true;
		}

		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "username")]
		public string Username { get; set; }

		[JsonProperty(PropertyName = "email")]
		public string Email { get; set; }

		/// <summary>
		/// Password hash — NEVER serialized for cross-service propagation.
		/// Only used within the Core service for credential validation.
		/// </summary>
		[JsonIgnore]
		public string Password { get; set; }

		[JsonProperty(PropertyName = "firstName")]
		public string FirstName { get; set; }

		[JsonProperty(PropertyName = "lastName")]
		public string LastName { get; set; }

		[JsonProperty(PropertyName = "image")]
		public string Image { get; set; }

		/// <summary>
		/// Whether the user account is active. Checked at login time only.
		/// Excluded from JSON serialization for security.
		/// </summary>
		[JsonIgnore]
		public bool Enabled { get; set; }

		/// <summary>
		/// Whether the user's email has been verified. Checked at login time only.
		/// Excluded from JSON serialization for security.
		/// </summary>
		[JsonIgnore]
		public bool Verified { get; set; }

		[JsonProperty(PropertyName = "createdOn")]
		public DateTime CreatedOn { get; set; }

		[JsonProperty(PropertyName = "lastLoggedIn")]
		public DateTime? LastLoggedIn { get; set; }

		/// <summary>
		/// Role membership list. Populated by SecurityManager on authentication;
		/// propagated across services via JWT role claims rather than direct
		/// serialization. Excluded from JSON to prevent leaking role details.
		/// </summary>
		[JsonIgnore]
		public List<ErpRole> Roles { get; private set; } = new List<ErpRole>();

		/// <summary>
		/// Computed property: true if the user holds the Administrator role.
		/// Uses <see cref="SystemIds.AdministratorRoleId"/> for the check.
		/// </summary>
		[JsonProperty(PropertyName = "is_admin")]
		public bool IsAdmin { get { return Roles.Any(x => x.Id == SystemIds.AdministratorRoleId); } }

		/// <summary>
		/// User-specific UI preferences (sidebar size, component usage tracking,
		/// component data dictionary).
		/// </summary>
		[JsonProperty(PropertyName = "preferences")]
		public ErpUserPreferences Preferences { get; set; }
	}

	/// <summary>
	/// User-specific preferences stored as a JSON document in the user record.
	/// Tracks UI state (sidebar size), component usage frequency, and
	/// per-component custom data.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.ErpUserPreferences</c>.
	/// </summary>
	[Serializable]
	public class ErpUserPreferences
	{
		[JsonProperty("sidebar_size")]
		public string SidebarSize { get; set; } = "";

		[JsonProperty("component_usage")]
		public List<UserComponentUsage> ComponentUsage { get; set; } = new List<UserComponentUsage>();

		[JsonProperty("component_data_dictionary")]
		public EntityRecord ComponentDataDictionary { get; set; } = new EntityRecord();

		/// <summary>
		/// Compares two preference instances for equality using JSON serialization.
		/// Used to detect whether user preferences have changed (dirty check).
		/// </summary>
		public bool Compare(ErpUserPreferences prefs)
		{
			if (prefs == null)
				return false;

			if (SidebarSize != prefs.SidebarSize)
				return false;

			if (JsonConvert.SerializeObject(ComponentUsage) != JsonConvert.SerializeObject(prefs.ComponentUsage))
				return false;

			if (JsonConvert.SerializeObject(ComponentDataDictionary) != JsonConvert.SerializeObject(prefs.ComponentDataDictionary))
				return false;

			return true;
		}
	}
}
