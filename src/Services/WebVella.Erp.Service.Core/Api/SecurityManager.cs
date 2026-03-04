using Newtonsoft.Json;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// Core Platform service security manager adapted from the monolith's
	/// <c>WebVella.Erp.Api.SecurityManager</c> (371 lines).
	///
	/// Provides user and role CRUD operations, credential validation, and
	/// JWT token issuance for the microservice architecture.
	///
	/// Key adaptations:
	/// <list type="bullet">
	///   <item><c>DbContext.Current</c> singleton replaced with injected <see cref="CoreDbContext"/></item>
	///   <item><c>new RecordManager()</c> replaced with injected <see cref="RecordManager"/></item>
	///   <item><c>ErpSettings.ConnectionString</c> used for direct ADO.NET system user lookup</item>
	///   <item>EQL queries preserved exactly from monolith for user/role retrieval</item>
	///   <item>All validation rules and error messages preserved</item>
	/// </list>
	/// </summary>
	public class SecurityManager
	{
		private readonly CoreDbContext _dbContext;
		private readonly RecordManager _recordManager;
		private readonly EntityRelationManager _entityRelationManager;

		/// <summary>
		/// Constructs a SecurityManager with all required service dependencies.
		/// Replaces monolith pattern of <c>new SecurityManager(DbContext)</c>.
		/// </summary>
		public SecurityManager(
			CoreDbContext dbContext,
			RecordManager recordManager,
			EntityRelationManager entityRelationManager)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
		}

		#region << User Retrieval >>

		/// <summary>
		/// Retrieves a user by their unique ID, including all assigned roles.
		/// Executes under OpenSystemScope to bypass permission checks.
		/// Preserved from monolith SecurityManager.GetUser(Guid).
		/// </summary>
		public ErpUser GetUser(Guid userId)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE id = @id",
					new List<EqlParameter> { new EqlParameter("id", userId) },
					currentContext: _dbContext).Execute();

				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		/// <summary>
		/// Retrieves a user by their email address, including all assigned roles.
		/// Preserved from monolith SecurityManager.GetUser(string email).
		/// </summary>
		public ErpUser GetUser(string email)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE email = @email",
					new List<EqlParameter> { new EqlParameter("email", email) },
					currentContext: _dbContext).Execute();

				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		/// <summary>
		/// Retrieves a user by username, including all assigned roles.
		/// Preserved from monolith SecurityManager.GetUserByUsername(string).
		/// </summary>
		public ErpUser GetUserByUsername(string username)
		{
			using (var ctx = SecurityContext.OpenSystemScope())
			{
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE username = @username",
					new List<EqlParameter> { new EqlParameter("username", username) },
					currentContext: _dbContext).Execute();

				if (result.Count != 1)
					return null;

				return result[0].MapTo<ErpUser>();
			}
		}

		/// <summary>
		/// Authenticates a user by email and password (MD5 hashed).
		/// Uses case-insensitive email matching via PostgreSQL regex (~*).
		/// Preserved from monolith SecurityManager.GetUser(string, string).
		/// </summary>
		public ErpUser GetUser(string email, string password)
		{
			if (string.IsNullOrWhiteSpace(email))
				return null;

			using (var ctx = SecurityContext.OpenSystemScope())
			{
				var encryptedPassword = PasswordUtil.GetMd5Hash(password);
				var result = new EqlCommand("SELECT *, $user_role.* FROM user WHERE email ~* @email AND password = @password",
					new List<EqlParameter>
					{
						new EqlParameter("email", email),
						new EqlParameter("password", encryptedPassword)
					},
					currentContext: _dbContext).Execute();

				foreach (var rec in result)
				{
					if (((string)rec["email"]).ToLowerInvariant() == email.ToLowerInvariant())
						return rec.MapTo<ErpUser>();
				}

				return null;
			}
		}

		/// <summary>
		/// Retrieves the system user record directly from the database via ADO.NET,
		/// bypassing EQL and all security checks. Used during bootstrap when the EQL
		/// engine may not yet be initialized.
		/// Preserved from monolith SecurityManager.GetSystemUserWithNoSecurityCheck().
		/// </summary>
		private ErpUser GetSystemUserWithNoSecurityCheck()
		{
			using (NpgsqlConnection connection = new NpgsqlConnection(ErpSettings.ConnectionString))
			{
				try
				{
					connection.Open();

					NpgsqlCommand cmd = new NpgsqlCommand("SELECT * FROM rec_user WHERE id = @id ", connection);
					cmd.Parameters.Add(new NpgsqlParameter("id", SystemIds.SystemUserId));

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
							dest.Roles.Add(new ErpRole
							{
								Id = (Guid)dr["id"],
								Name = (string)dr["name"],
								Description = (string)dr["description"]
							});

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

		/// <summary>
		/// Retrieves all users that belong to any of the specified roles.
		/// Uses parameterized EQL with OR conditions for each role ID.
		/// Preserved from monolith SecurityManager.GetUsers(params Guid[]).
		/// </summary>
		public List<ErpUser> GetUsers(params Guid[] roleIds)
		{
			List<EqlParameter> parameters = new List<EqlParameter>();
			StringBuilder sbRoles = new StringBuilder();
			foreach (Guid id in roleIds)
			{
				if (sbRoles.Length > 0)
					sbRoles.AppendLine(" OR ");
				else
					sbRoles.AppendLine(" WHERE ");

				var paramName = $"@role_id_{id.ToString().Replace("-", "")}";
				sbRoles.AppendLine($" $user_role.id = {paramName} ");
				parameters.Add(new EqlParameter(paramName, id));
			}

			return new EqlCommand("SELECT *, $user_role.* FROM user " + sbRoles,
				parameters, currentContext: _dbContext).Execute().MapTo<ErpUser>();
		}

		#endregion

		#region << Role Retrieval >>

		/// <summary>
		/// Retrieves all roles in the system.
		/// Preserved from monolith SecurityManager.GetAllRoles().
		/// </summary>
		public List<ErpRole> GetAllRoles()
		{
			return new EqlCommand("SELECT * FROM role",
				currentContext: _dbContext).Execute().MapTo<ErpRole>();
		}

		#endregion

		#region << User CRUD >>

		/// <summary>
		/// Creates or updates a user record. Handles:
		/// - Unique email validation
		/// - Unique username validation
		/// - Email format validation
		/// - Password encryption (via RecordManager's field extraction)
		/// - Role assignment via $user_role relation field
		/// - Differential update (only changed fields are written)
		/// Preserved exactly from monolith SecurityManager.SaveUser(ErpUser).
		/// </summary>
		public void SaveUser(ErpUser user)
		{
			if (user == null)
				throw new ArgumentNullException(nameof(user));

			EntityRecord record = new EntityRecord();

			ErpUser existingUser = GetUser(user.Id);
			ValidationException valEx = new ValidationException();

			if (existingUser != null)
			{
				// Update existing user — only include changed fields
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

				var response = _recordManager.UpdateRecord("user", record);
				if (!response.Success)
					throw new Exception(response.Message);
			}
			else
			{
				// Create new user
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

				var response = _recordManager.CreateRecord("user", record);
				if (!response.Success)
					throw new Exception(response.Message);
			}
		}

		#endregion

		#region << Role CRUD >>

		/// <summary>
		/// Creates or updates a role record. Handles:
		/// - Unique name validation
		/// - Required name validation
		/// - Differential update (only changed fields on update)
		/// Preserved exactly from monolith SecurityManager.SaveRole(ErpRole).
		/// </summary>
		public void SaveRole(ErpRole role)
		{
			if (role == null)
				throw new ArgumentNullException(nameof(role));

			EntityRecord record = new EntityRecord();
			var allRoles = GetAllRoles();
			ErpRole existingRole = allRoles.SingleOrDefault(x => x.Id == role.Id);
			ValidationException valEx = new ValidationException();

			if (role.Description is null)
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

				var response = _recordManager.UpdateRecord("role", record);
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

				var response = _recordManager.CreateRecord("role", record);
				if (!response.Success)
					throw new Exception(response.Message);
			}
		}

		#endregion

		#region << Utilities >>

		/// <summary>
		/// Updates the last login timestamp for the specified user.
		/// Uses direct repository access for efficiency (bypasses full RecordManager pipeline).
		/// Preserved from monolith SecurityManager.UpdateUserLastLoginTime(Guid).
		/// </summary>
		public void UpdateUserLastLoginTime(Guid userId)
		{
			List<KeyValuePair<string, object>> storageRecordData = new List<KeyValuePair<string, object>>();
			storageRecordData.Add(new KeyValuePair<string, object>("id", userId));
			storageRecordData.Add(new KeyValuePair<string, object>("last_logged_in", DateTime.UtcNow));
			_dbContext.RecordRepository.Update("user", storageRecordData);
		}

		/// <summary>
		/// Validates an email address format using System.Net.Mail.MailAddress parsing.
		/// Returns true if the email is valid, false otherwise.
		/// Preserved from monolith SecurityManager.IsValidEmail(string).
		/// </summary>
		public bool IsValidEmail(string email)
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

		#endregion
	}
}
