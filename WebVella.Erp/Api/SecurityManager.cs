using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Api.Models.AutoMapper;
using WebVella.Erp.Database;
using WebVella.Erp.Diagnostics;
using WebVella.Erp.Eql;
using WebVella.Erp.Exceptions;
using WebVella.Erp.Utilities;

namespace WebVella.Erp.Api
{
	public class SecurityManager
	{
		private DbContext suppliedContext = null;
		private DbContext CurrentContext
		{
			get
			{
				if (suppliedContext != null)
					return suppliedContext;
				else
					return DbContext.Current;
			}
		}
		public SecurityManager(DbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		public ErpUser GetUser(Guid userId)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE id = @id",
				new List<EqlParameter> { new EqlParameter("id", userId) }).Execute();
				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		public ErpUser GetUser(string email)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{

				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE email = @email",
				 new List<EqlParameter> { new EqlParameter("email", email) }).Execute();
				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		public ErpUser GetUserByUsername(string username)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{

				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE username = @username",
				 new List<EqlParameter> { new EqlParameter("username", username) }).Execute();
				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		public ErpUser GetUser(string email, string password)
		{
			if (string.IsNullOrWhiteSpace(email))
				return null;

			using (var ctx = SecurityContext.OpenSystemScope())
			{
				// Security fix: F-002 — PBKDF2 hashes use a fresh random salt per invocation, so
				// the legacy EQL "password = @password" equality comparison no longer works.
				// Instead, fetch user candidates by email only and verify the supplied password
				// against each stored hash via PasswordUtil.VerifyMd5Hash, which transparently
				// supports both the new pbkdf2$... format and legacy 32-hex-char MD5 hashes.
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE email ~* @email",
						 new List<EqlParameter> { new EqlParameter("email", email) }).Execute();

				foreach (var rec in result)
				{
					if (((string)rec["email"]).ToLowerInvariant() != email.ToLowerInvariant())
						continue;

					// Security fix: F-002 — Use VerifyMd5Hash (PBKDF2 + legacy MD5 fallback) instead of EQL equality.
					var storedHash = rec["password"] as string;
					if (PasswordUtil.VerifyMd5Hash(password, storedHash))
						return rec.MapTo<ErpUser>();
				}

				return null;
			}
		}

		private ErpUser GetSystemUserWithNoSecurityCheck()
		{
			using (NpgsqlConnection connection = new NpgsqlConnection(ErpSettings.ConnectionString))
			{
				try
				{
					connection.Open();

					NpgsqlCommand cmd = new NpgsqlCommand("SELECT * FROM rec_user WHERE id = @id ", connection);
					cmd.Parameters.Add(new NpgsqlParameter("id", SystemIds.SystemUserId ));

					NpgsqlDataAdapter dataAdapter = new NpgsqlDataAdapter(cmd);
					DataTable dt = new DataTable();
					dataAdapter.Fill(dt);

					if (dt.Rows.Count > 0)
					{
						DataRow src = dt.Rows[0];

						ErpUser dest = new ErpUser();
						dest.Id = (Guid)src["id"];
						dest.Username = (string)src["username"];
						dest.Email = (string)src["email"];

						try
						{
							dest.Password = (string)src["password"];
						}
						catch (KeyNotFoundException)
						{
							//set password to null if it is not selected from DB
							dest.Password = null;
						}

						dest.FirstName = (string)src["first_name"];
						dest.LastName = (string)src["last_name"];
						dest.Image = (string)src["image"];
						dest.CreatedOn = (DateTime)src["created_on"];
						dest.LastLoggedIn = (DateTime?)src["last_logged_in"];
						dest.Enabled = (bool)src["enabled"];
						dest.Verified = (bool)src["verified"];

						cmd = new NpgsqlCommand(@"SELECT r.* FROM rec_role r
								LEFT OUTER JOIN rel_user_role ur ON ur.origin_id = r.id
								WHERE ur.target_id = @user_id ", connection);
						cmd.Parameters.Add(new NpgsqlParameter("user_id", dest.Id));
						dataAdapter = new NpgsqlDataAdapter(cmd);
						dt = new DataTable();
						dataAdapter.Fill(dt);

						foreach (DataRow dr in dt.Rows)
							dest.Roles.Add(new ErpRole { Id = (Guid)dr["id"], Name = (string)dr["name"], Description = (string)dr["description"] });

						return dest;
					}
					else
					{
						return null;
					}

				}
				finally
				{
					connection.Close();
				}

			}
		}

		public List<ErpUser> GetUsers(params Guid[] roleIds)
		{
			List<EqlParameter> parameters = new List<EqlParameter>();
			StringBuilder sbRoles = new StringBuilder();
			foreach (var id in roleIds)
			{
				if (sbRoles.Length > 0)
					sbRoles.AppendLine(" OR ");
				else
					sbRoles.AppendLine(" WHERE ");

				var paramName = $"@role_id_{id.ToString().Replace("-", "")}";
				sbRoles.AppendLine($" $user_role.id = {paramName} ");
				parameters.Add(new EqlParameter(paramName, id));
			}

			return new EqlCommand("SELECT *, $user_role.* FROM user " + sbRoles, parameters).Execute().MapTo<ErpUser>();
		}

		public List<ErpRole> GetAllRoles()
		{
			return new EqlCommand("SELECT * FROM role").Execute().MapTo<ErpRole>();
		}

		public void SaveUser(ErpUser user)
		{
			if (user == null)
				throw new ArgumentNullException(nameof(user));

			RecordManager recMan = new RecordManager();
			EntityRelationManager relMan = new EntityRelationManager(CurrentContext);
			EntityRecord record = new EntityRecord();

			ErpUser existingUser = GetUser(user.Id);
			ValidationException valEx = new ValidationException();
			if (existingUser != null)
			{
				record["id"] = user.Id;

				if (existingUser.Username != user.Username)
				{
					record["username"] = user.Username;

					if (string.IsNullOrWhiteSpace(user.Username))
						valEx.AddError("username", "Username is required.");
					else if (GetUserByUsername(user.Username) != null)
						valEx.AddError("username", "Username is already registered to another user. It must be unique.");
				}

				if (existingUser.Email != user.Email)
				{
					record["email"] = user.Email;

					if (string.IsNullOrWhiteSpace(user.Email))
						valEx.AddError("email", "Email is required.");
					else if (GetUser(user.Email) != null)
						valEx.AddError("email", "Email is already registered to another user. It must be unique.");
					else if (!IsValidEmail(user.Email))
						valEx.AddError("email", "Email is not valid.");
				}

				if (existingUser.Password != user.Password && !string.IsNullOrWhiteSpace(user.Password))
					record["password"] = user.Password;

				if (existingUser.Enabled != user.Enabled)
					record["enabled"] = user.Enabled;

				if (existingUser.Verified != user.Verified)
					record["verified"] = user.Verified;

				if (existingUser.FirstName != user.FirstName)
					record["first_name"] = user.FirstName;

				if (existingUser.LastName != user.LastName)
					record["last_name"] = user.LastName;

				if (existingUser.Image != user.Image)
					record["image"] = user.Image;

				record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList();

				valEx.CheckAndThrow();

				var response = recMan.UpdateRecord("user", record);
				if (!response.Success)
					throw new Exception(response.Message);

			}
			else
			{
				record["id"] = user.Id;
				record["email"] = user.Email;
				record["username"] = user.Username;
				record["first_name"] = user.FirstName;
				record["last_name"] = user.LastName;
				record["enabled"] = user.Enabled;
				record["verified"] = user.Verified;
				record["image"] = user.Image;
				record["preferences"] = JsonConvert.SerializeObject(user.Preferences ?? new ErpUserPreferences());

				if (string.IsNullOrWhiteSpace(user.Username))
					valEx.AddError("username", "Username is required.");
				else if (GetUserByUsername(user.Username) != null)
					valEx.AddError("username", "Username is already registered to another user. It must be unique.");

				if (string.IsNullOrWhiteSpace(user.Email))
					valEx.AddError("email", "Email is required.");
				else if (GetUser(user.Email) != null)
					valEx.AddError("email", "Email is already registered to another user. It must be unique.");
				else if (!IsValidEmail(user.Email))
					valEx.AddError("email", "Email is not valid.");

				if (string.IsNullOrWhiteSpace(user.Password))
					valEx.AddError("password", "Password is required.");
				else
					record["password"] = user.Password;

				record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList();

				valEx.CheckAndThrow();

				var response = recMan.CreateRecord("user", record);
				if (!response.Success)
					throw new Exception(response.Message);

			}
		}

		public void SaveRole(ErpRole role)
		{
			if (role == null)
				throw new ArgumentNullException(nameof(role));

			RecordManager recMan = new RecordManager();
			EntityRecord record = new EntityRecord();
			var allRoles = GetAllRoles();
			ErpRole existingRole = allRoles.SingleOrDefault(x => x.Id == role.Id);
			ValidationException valEx = new ValidationException();
			if(role.Description is null)
				role.Description = String.Empty;
			if (existingRole != null)
			{
				record["id"] = role.Id;
				record["description"] = role.Description;

				if (existingRole.Name != role.Name)
				{
					record["name"] = role.Name;

					if (string.IsNullOrWhiteSpace(role.Name))
						valEx.AddError("name", "Name is required.");
					else if (allRoles.Any(x => x.Name == role.Name))
						valEx.AddError("name", "Role with same name already exists");
				}

				valEx.CheckAndThrow();

				var response = recMan.UpdateRecord("role", record);
				if (!response.Success)
					throw new Exception(response.Message);

			}
			else
			{
				record["id"] = role.Id;
				record["description"] = role.Description;
				record["name"] = role.Name;

				if (string.IsNullOrWhiteSpace(role.Name))
					valEx.AddError("name", "Name is required.");
				else if (allRoles.Any(x => x.Name == role.Name))
					valEx.AddError("name", "Role with same name already exists");

				valEx.CheckAndThrow();

				var response = recMan.CreateRecord("role", record);
				if (!response.Success)
					throw new Exception(response.Message);

			}
		}


		public void UpdateUserLastLoginTime(Guid userId)
		{
			List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();
			storageRecordData.Add(new KeyValuePair<string, object>("id", userId));
			storageRecordData.Add(new KeyValuePair<string, object>("last_logged_in", DateTime.UtcNow));
			CurrentContext.RecordRepository.Update("user", storageRecordData);
		}

		private bool IsValidEmail(string email)
		{
			try
			{
				var addr = new System.Net.Mail.MailAddress(email);
				return addr.Address == email;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Generates a cryptographically secure 32-character random password suitable for
		/// seeding a default administrator account at first run.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Security fix F-003 — CWE-798 Use of Hard-coded Credentials.
		/// This helper replaces literal hardcoded admin passwords (e.g., <c>"erp"</c>) in seed
		/// code paths. Operators must invoke this from any seed/bootstrap code (currently
		/// <c>WebVella.Erp.ERPService.InitializeSystemEntities</c>) and capture the returned
		/// plaintext password from the application console at first run.
		/// </para>
		/// <para>
		/// The password is derived from 24 cryptographically random bytes
		/// (<see cref="RandomNumberGenerator.GetBytes(int)"/>, 192 bits of entropy) and
		/// encoded as 32 URL-safe Base64 characters.
		/// </para>
		/// </remarks>
		/// <returns>A 32-character URL-safe Base64-encoded random password.</returns>
		// Security fix: F-003 — Helper for replacing hardcoded admin password with cryptographically random first-run password.
		public static string GenerateInitialAdminPassword()
		{
			byte[] randomBytes = RandomNumberGenerator.GetBytes(24);
			string base64 = Convert.ToBase64String(randomBytes);
			// URL-safe Base64: substitute non-URL-safe characters and strip any padding.
			return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
		}

		/// <summary>
		/// Flags a user as requiring a password change on next login.
		/// </summary>
		/// <param name="userId">The identifier of the user to flag.</param>
		/// <remarks>
		/// <para>
		/// Security fix F-003 — CWE-798 Use of Hard-coded Credentials.
		/// This helper is intended to be invoked by seed/bootstrap code immediately after a
		/// default administrator is provisioned with a randomly generated password (see
		/// <see cref="GenerateInitialAdminPassword"/>). When invoked, it should set a
		/// <c>MustChangePassword</c> (or equivalent) flag on the user record so that the
		/// authentication flow forces the operator to rotate the password on first login.
		/// </para>
		/// <para>
		/// The current <c>user</c> entity schema does not yet expose a
		/// <c>MustChangePassword</c> column, so this helper logs a warning via
		/// <see cref="WebVella.Erp.Diagnostics.Log"/> and documents the limitation. Adding
		/// the column constitutes a schema modification that is out of scope for the
		/// Critical-only F-003 remediation; it is recorded as a follow-up engagement step in
		/// <c>/docs/security/pentest-findings.md</c>.
		/// </para>
		/// </remarks>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="userId"/> is <see cref="Guid.Empty"/> or refers to a
		/// user that does not exist.
		/// </exception>
		// Security fix: F-003 — Helper to flag a user for required password change on next login.
		public void RequirePasswordChangeOnNextLogin(Guid userId)
		{
			if (userId == Guid.Empty)
				throw new ArgumentException("User identifier is required.", nameof(userId));

			ErpUser user = GetUser(userId);
			if (user == null)
				throw new ArgumentException($"User '{userId}' not found.", nameof(userId));

			// Schema does not yet expose a MustChangePassword column on the user entity.
			// Adding the column is a schema modification and is out of scope for the
			// Critical-only F-003 remediation. Log a warning so operators are aware that the
			// user requires manual password rotation until the column is added in a
			// follow-up engagement.
			Log log = new Log();
			log.Create(
				LogType.Error,
				"SecurityManager.RequirePasswordChangeOnNextLogin",
				"MustChangePassword flag could not be set: schema does not yet expose this column. Operator must rotate the password manually.",
				$"userId={userId}");
		}
	}
}
