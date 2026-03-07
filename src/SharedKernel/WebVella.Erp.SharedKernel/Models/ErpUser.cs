using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace WebVella.Erp.SharedKernel.Models
{
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
			Claims = new Dictionary<string, string>();
		}

        [JsonProperty(PropertyName = "id")]
        public Guid Id { get; set; }

		[JsonProperty(PropertyName = "username")]
		public string Username { get; set; }
		
		[JsonProperty(PropertyName = "email")]
        public string Email { get; set; }

		[JsonIgnore]
		//[JsonProperty(PropertyName = "password")]
		public string Password { get; set; }

        [JsonProperty(PropertyName = "firstName")]
        public string FirstName { get; set; }

        [JsonProperty(PropertyName = "lastName")]
        public string LastName { get; set; }

        [JsonProperty(PropertyName = "image")]
        public string Image { get; set; }

		[JsonIgnore]
		//[JsonProperty(PropertyName = "enabled")]
        public bool Enabled { get; set; }

		[JsonIgnore]
		//[JsonProperty(PropertyName = "verified")]
		public bool Verified { get; set; }

		[JsonProperty(PropertyName = "createdOn")]
		public DateTime CreatedOn { get; set; }

		[JsonProperty(PropertyName = "lastLoggedIn")]
        public DateTime? LastLoggedIn { get; set; }

		[JsonIgnore]
		//[JsonProperty(PropertyName = "roles")]
		public List<ErpRole> Roles { get; private set; } = new List<ErpRole>();

		[JsonProperty(PropertyName = "is_admin")]
		public bool IsAdmin { get { return Roles.Any(x => x.Id == SystemIds.AdministratorRoleId); } }
	
		[JsonProperty(PropertyName = "preferences")]
		public ErpUserPreferences Preferences { get; set; }

		[JsonProperty(PropertyName = "claims")]
		public Dictionary<string, string> Claims { get; set; }

		/// <summary>
		/// Creates an ErpUser instance from a set of JWT claims.
		/// Used for cross-service identity propagation where a downstream
		/// service reconstructs the user from the claims in the incoming JWT token.
		/// </summary>
		/// <param name="claims">The JWT claims to map into an ErpUser.</param>
		/// <returns>A populated ErpUser instance.</returns>
		public static ErpUser FromClaims(IEnumerable<Claim> claims)
		{
			if (claims == null)
				throw new ArgumentNullException(nameof(claims));

			var claimsList = claims.ToList();
			var user = new ErpUser();

			foreach (var claim in claimsList)
			{
				if (claim.Type == ClaimTypes.NameIdentifier)
				{
					if (Guid.TryParse(claim.Value, out Guid userId))
						user.Id = userId;
				}
				else if (claim.Type == ClaimTypes.Name)
				{
					user.Username = claim.Value;
				}
				else if (claim.Type == ClaimTypes.Email)
				{
					user.Email = claim.Value;
				}
				else if (claim.Type == ClaimTypes.GivenName)
				{
					user.FirstName = claim.Value;
				}
				else if (claim.Type == ClaimTypes.Surname)
				{
					user.LastName = claim.Value;
				}
				else if (claim.Type == "image")
				{
					user.Image = claim.Value;
				}
				else if (claim.Type == ClaimTypes.Role)
				{
					if (Guid.TryParse(claim.Value, out Guid roleId))
					{
						user.Roles.Add(new ErpRole { Id = roleId });
					}
				}
				else if (claim.Type == "role_name")
				{
					// Match role names to already-added roles by order.
					// role_name claims are expected to follow their corresponding Role claims.
					var roleWithoutName = user.Roles.FirstOrDefault(r => string.IsNullOrEmpty(r.Name));
					if (roleWithoutName != null)
					{
						roleWithoutName.Name = claim.Value;
					}
				}

				// Populate the Claims dictionary with all claim type-value pairs.
				// If multiple claims share the same type, last value wins in the dictionary.
				user.Claims[claim.Type] = claim.Value;
			}

			return user;
		}

		/// <summary>
		/// Converts this ErpUser into a list of JWT claims for cross-service
		/// identity propagation. The resulting claims can be embedded in a JWT token
		/// to carry user identity across microservice boundaries.
		/// </summary>
		/// <returns>A list of claims representing this user.</returns>
		public List<Claim> ToClaims()
		{
			var claimsList = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, Id.ToString()),
				new Claim(ClaimTypes.Name, Username ?? string.Empty),
				new Claim(ClaimTypes.Email, Email ?? string.Empty),
				new Claim(ClaimTypes.GivenName, FirstName ?? string.Empty),
				new Claim(ClaimTypes.Surname, LastName ?? string.Empty)
			};

			if (!string.IsNullOrEmpty(Image))
			{
				claimsList.Add(new Claim("image", Image));
			}

			foreach (var role in Roles)
			{
				claimsList.Add(new Claim(ClaimTypes.Role, role.Id.ToString()));

				if (!string.IsNullOrEmpty(role.Name))
				{
					claimsList.Add(new Claim("role_name", role.Name));
				}
			}

			// Append custom claims from the Claims dictionary, avoiding duplicates
			// with the standard claims already added above.
			if (Claims != null)
			{
				var existingTypes = new HashSet<string>(claimsList.Select(c => c.Type));
				foreach (var kvp in Claims)
				{
					if (!existingTypes.Contains(kvp.Key))
					{
						claimsList.Add(new Claim(kvp.Key, kvp.Value ?? string.Empty));
					}
				}
			}

			return claimsList;
		}
	}
}
