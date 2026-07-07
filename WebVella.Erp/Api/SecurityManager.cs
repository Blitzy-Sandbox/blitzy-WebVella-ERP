using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Api.Models.AutoMapper;
using WebVella.Erp.Database;
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
				// SECURITY (A02/CWE-916 + A07/CWE-287): unsalted MD5 is a broken password primitive and, because the new
				// PBKDF2 hashes are salted and non-deterministic, a SQL "password = @password" equality match is no longer
				// possible. Fetch the user by email ONLY - the email stays parameterized (A03 SQL-injection protection is
				// preserved) and keeps the case-insensitive ~* regex operator for behavioral parity - then verify in code below.
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE email ~* @email",
						 new List<EqlParameter> { new EqlParameter("email", email) }).Execute();

				foreach (var rec in result)
				{
					if (((string)rec["email"]).ToLowerInvariant() == email.ToLowerInvariant())
					{
						// SECURITY (A02/CWE-916 + A07/CWE-287): PBKDF2 hashes are salted/non-deterministic and can no longer be
						// matched by SQL equality, so credential verification is performed in code here. Read the STORED hash from
						// the raw record - ErpUser.Password is [JsonIgnore] (Api/Models/ErpUser.cs) and may be null after MapTo.
						var storedHash = rec["password"] as string;
						var vr = PasswordUtil.VerifyPassword(storedHash, password);

						if (vr == PasswordUtil.PasswordVerificationResult.Failed)
						{
							// SECURITY (A09/CWE-778): best-effort, non-throwing auth-failure audit (never logs the supplied password).
							try { new WebVella.Erp.Diagnostics.Log().LogAuthenticationFailure(email); } catch { }
							return null;
						}

						if (vr == PasswordUtil.PasswordVerificationResult.SuccessRehashNeeded)
						{
							// SECURITY (A02/CWE-916): transparently re-hash a verified LEGACY MD5 credential to PBKDF2 (rehash-on-login)
							// so no existing user is locked out and MD5 is phased out over time. A targeted, parameterized direct UPDATE
							// is used so the value is NOT hashed a second time (no double-hash) and no record hooks/validation fire on the
							// login hot path. Mirrors the direct-SQL idiom in GetSystemUserWithNoSecurityCheck (physical table rec_user).
							try
							{
								var newHash = PasswordUtil.HashPassword(password);
								using (NpgsqlConnection connection = new NpgsqlConnection(ErpSettings.ConnectionString))
								{
									connection.Open();
									using (NpgsqlCommand cmd = new NpgsqlCommand("UPDATE rec_user SET password = @password WHERE id = @id", connection))
									{
										cmd.Parameters.AddWithValue("@password", newHash);
										cmd.Parameters.AddWithValue("@id", (Guid)rec["id"]);
										cmd.ExecuteNonQuery();
									}
								}
							}
							catch { /* a write-back failure must NOT block an otherwise successful login */ }
						}

						return rec.MapTo<ErpUser>();
					}
				}

				// SECURITY (A09/CWE-778): best-effort, non-throwing audit for a login attempt against a non-existent/non-matching email.
				try { new WebVella.Erp.Diagnostics.Log().LogAuthenticationFailure(email); } catch { }
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
	}
}
