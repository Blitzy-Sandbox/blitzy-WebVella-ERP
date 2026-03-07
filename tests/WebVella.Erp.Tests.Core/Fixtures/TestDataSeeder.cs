using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;

namespace WebVella.Erp.Tests.Core.Fixtures
{
	/// <summary>
	/// Seeds the test database with the same system entities, default users, roles,
	/// and sample data that the monolith's <c>ERPService.InitializeSystemEntities()</c>
	/// creates. This ensures tests operate against a database state identical to a
	/// freshly initialized production database.
	///
	/// Every entity ID, field ID, user ID, role ID, and relation ID matches the
	/// monolith's <c>ERPService.cs</c> and <c>Definitions.cs</c> exactly.
	///
	/// Seeding is idempotent: calling <see cref="SeedAsync"/> multiple times will
	/// not fail or create duplicate records.
	/// </summary>
	public class TestDataSeeder
	{
		private readonly IServiceProvider _serviceProvider;

		/// <summary>
		/// Constructs a new <see cref="TestDataSeeder"/> with the test DI container's
		/// service provider. All Core service managers (EntityManager, RecordManager,
		/// EntityRelationManager) and infrastructure (CoreDbContext, IConfiguration)
		/// are resolved from this provider during seeding.
		/// </summary>
		/// <param name="serviceProvider">
		/// The test DI service provider containing registered Core service managers,
		/// CoreDbContext, and IConfiguration with a valid PostgreSQL connection string
		/// at <c>ConnectionStrings:Default</c>.
		/// </param>
		public TestDataSeeder(IServiceProvider serviceProvider)
		{
			_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
		}

		/// <summary>
		/// Orchestrates the complete database seeding process, mirroring the monolith's
		/// <c>ERPService.InitializeSystemEntities()</c> method (ERPService.cs lines 18-527).
		///
		/// Execution order:
		/// 1. Create PostgreSQL extensions and system tables (raw SQL)
		/// 2. Create system entities: user, role, user_file (via EntityManager)
		/// 3. Create default users and roles (via RecordManager)
		/// 4. Link users to roles (via RecordManager)
		/// 5. Create sample test entities for diverse field-type testing
		/// 6. Create sample test relations for relation query testing
		///
		/// All operations run inside a <see cref="SecurityContext.OpenSystemScope"/>
		/// to bypass permission checks, matching the monolith's initialization pattern.
		/// </summary>
		public async Task SeedAsync()
		{
			await EnsureDatabaseSchemaAsync();

			var connectionString = GetConnectionString();
			CoreDbContext.CreateContext(connectionString);
			try
			{
				using (SecurityContext.OpenSystemScope())
				{
					await SeedSystemEntitiesAsync();
					await SeedDefaultUsersAndRolesAsync();
					await SeedUserRoleRelationsAsync();
					await SeedSampleEntitiesAsync();
					await SeedSampleRelationsAsync();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		#region << EnsureDatabaseSchemaAsync >>

		/// <summary>
		/// Creates PostgreSQL extensions and system tables required by the ERP engine.
		/// Matches <c>DbRepository.CreatePostgresqlExtensions()</c> (ERPService.cs line 28)
		/// and <c>CheckCreateSystemTables()</c> (ERPService.cs line 36).
		/// </summary>
		private async Task EnsureDatabaseSchemaAsync()
		{
			// PostgreSQL extensions (ERPService.cs line 28)
			await ExecuteSqlAsync("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
			await ExecuteSqlAsync("CREATE EXTENSION IF NOT EXISTS \"pg_trgm\";");

			// entities table (ERPService.cs lines 935-938)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'entities') THEN
						CREATE TABLE public.entities (
							id uuid NOT NULL,
							json json NOT NULL,
							CONSTRAINT entities_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// entity_relations table (ERPService.cs lines 950-953)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'entity_relations') THEN
						CREATE TABLE public.entity_relations (
							id uuid NOT NULL,
							json json NOT NULL,
							CONSTRAINT entity_relations_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// system_settings table (ERPService.cs lines 966-969)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'system_settings') THEN
						CREATE TABLE public.system_settings (
							id uuid NOT NULL,
							version integer NOT NULL,
							CONSTRAINT system_settings_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// plugin_data table (used by plugin persistence)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'plugin_data') THEN
						CREATE TABLE public.plugin_data (
							id uuid NOT NULL,
							json json NOT NULL,
							CONSTRAINT plugin_data_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// system_log table (diagnostics)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'system_log') THEN
						CREATE TABLE public.system_log (
							id uuid NOT NULL,
							created_on timestamp without time zone NOT NULL DEFAULT now(),
							type integer NOT NULL DEFAULT 0,
							source text,
							message text,
							details text,
							CONSTRAINT system_log_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// system_search table (ERPService.cs lines 984-1003)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'system_search') THEN
						CREATE TABLE public.system_search (
							id UUID NOT NULL,
							entities TEXT DEFAULT ''::text NOT NULL,
							apps TEXT DEFAULT ''::text NOT NULL,
							records TEXT DEFAULT ''::text NOT NULL,
							content TEXT DEFAULT ''::text NOT NULL,
							snippet TEXT DEFAULT ''::text NOT NULL,
							url TEXT DEFAULT ''::text NOT NULL,
							aux_data TEXT DEFAULT ''::text NOT NULL,
							""timestamp"" TIMESTAMP(0) WITH TIME ZONE NOT NULL,
							stem_content TEXT DEFAULT ''::text NOT NULL,
							CONSTRAINT system_search_pkey PRIMARY KEY(id)
						);
						CREATE INDEX IF NOT EXISTS system_search_fts_idx
							ON system_search USING gin(to_tsvector('english', stem_content));
					END IF;
				END $$;");

			// files table (ERPService.cs lines 1018-1041)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'files') THEN
						CREATE TABLE public.files (
							id uuid NOT NULL,
							object_id numeric(18) NOT NULL,
							filepath text NOT NULL,
							created_on timestamp WITHOUT TIME ZONE NOT NULL,
							modified_on timestamp WITHOUT TIME ZONE NOT NULL,
							created_by uuid,
							modified_by uuid,
							CONSTRAINT files_pkey PRIMARY KEY (id),
							CONSTRAINT udx_filepath UNIQUE (filepath)
						);
						CREATE INDEX IF NOT EXISTS idx_filepath ON files (filepath);
					END IF;
				END $$;");

			// jobs table (used by the job system)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'jobs') THEN
						CREATE TABLE public.jobs (
							id uuid NOT NULL,
							type_id uuid NOT NULL,
							type_name text,
							assembly text,
							complete_class_name text,
							attributes text,
							status integer NOT NULL DEFAULT 0,
							priority integer NOT NULL DEFAULT 1,
							started_on timestamp without time zone,
							finished_on timestamp without time zone,
							aborted_by text,
							canceled_by text,
							created_on timestamp without time zone NOT NULL DEFAULT now(),
							created_by uuid,
							result text,
							error_message text,
							CONSTRAINT jobs_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");

			// schedules table (used by the schedule system)
			await ExecuteSqlAsync(@"
				DO $$ BEGIN
					IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'schedule_plans') THEN
						CREATE TABLE public.schedule_plans (
							id uuid NOT NULL,
							json json NOT NULL,
							CONSTRAINT schedule_plans_pkey PRIMARY KEY (id)
						);
					END IF;
				END $$;");
		}

		#endregion

		#region << SeedSystemEntitiesAsync >>

		/// <summary>
		/// Creates the three system entities (user, role, user_file) with all fields
		/// exactly matching <c>ERPService.InitializeSystemEntities()</c> lines 58-862.
		/// Idempotent: checks for existing entities before creation.
		/// </summary>
		private async Task SeedSystemEntitiesAsync()
		{
			var entMan = _serviceProvider.GetRequiredService<EntityManager>();

			await SeedUserEntityAsync(entMan);
			await SeedRoleEntityAsync(entMan);
			await SeedUserFileEntityAsync(entMan);
		}

		/// <summary>
		/// Creates the user entity with all 11 fields matching ERPService.cs lines 58-342.
		/// Entity ID: <c>b9cebc3b-6443-452a-8e34-b311a73dcc8b</c> (SystemIds.UserEntityId).
		/// </summary>
		private async Task SeedUserEntityAsync(EntityManager entMan)
		{
			// Idempotency check
			var existingEntity = entMan.ReadEntity(SystemIds.UserEntityId);
			if (existingEntity.Object != null)
				return;

			// Create user entity (ERPService.cs lines 58-84)
			var userEntity = new InputEntity();
			userEntity.Id = SystemIds.UserEntityId;
			userEntity.Name = "user";
			userEntity.Label = "User";
			userEntity.LabelPlural = "Users";
			userEntity.System = true;
			userEntity.Color = "#f44336";
			userEntity.IconName = "fa fa-user";
			userEntity.RecordPermissions = new RecordPermissions();
			userEntity.RecordPermissions.CanCreate = new List<Guid>();
			userEntity.RecordPermissions.CanRead = new List<Guid>();
			userEntity.RecordPermissions.CanUpdate = new List<Guid>();
			userEntity.RecordPermissions.CanDelete = new List<Guid>();
			userEntity.RecordPermissions.CanCreate.Add(SystemIds.GuestRoleId);
			userEntity.RecordPermissions.CanCreate.Add(SystemIds.AdministratorRoleId);
			userEntity.RecordPermissions.CanRead.Add(SystemIds.GuestRoleId);
			userEntity.RecordPermissions.CanRead.Add(SystemIds.RegularRoleId);
			userEntity.RecordPermissions.CanRead.Add(SystemIds.AdministratorRoleId);
			userEntity.RecordPermissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
			userEntity.RecordPermissions.CanDelete.Add(SystemIds.AdministratorRoleId);

			var createResponse = entMan.CreateEntity(userEntity, true, false);
			if (!createResponse.Success)
				throw new Exception("Failed to create user entity: " + createResponse.Message);

			var entityId = SystemIds.UserEntityId;

			// created_on field (ERPService.cs lines 86-107)
			{
				var field = new InputDateTimeField();
				field.Id = new Guid("6fda5e6b-80e6-4d8a-9e2a-d983c3694e96");
				field.Name = "created_on";
				field.Label = "Created On";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = true;
				field.System = true;
				field.DefaultValue = null;
				field.Format = "dd MMM yyyy HH:mm:ss";
				field.UseCurrentTimeAsDefaultValue = true;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: created_on. " + resp.Message);
			}

			// first_name field (ERPService.cs lines 110-131)
			{
				var field = new InputTextField();
				field.Id = new Guid("DF211549-41CC-4D11-BB43-DACA4C164411");
				field.Name = "first_name";
				field.Label = "First Name";
				field.PlaceholderText = "";
				field.Description = "First name of the user";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "";
				field.MaxLength = 200;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: first_name. " + resp.Message);
			}

			// last_name field (ERPService.cs lines 133-154)
			{
				var field = new InputTextField();
				field.Id = new Guid("63E685B1-B2C6-4961-B393-2B6723EBD1BF");
				field.Name = "last_name";
				field.Label = "Last Name";
				field.PlaceholderText = "";
				field.Description = "Last name of the user";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "";
				field.MaxLength = 200;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: last_name. " + resp.Message);
			}

			// username field (ERPService.cs lines 156-177)
			{
				var field = new InputTextField();
				field.Id = new Guid("263c0b21-88c1-4c2b-80b4-db7402b0d2e2");
				field.Name = "username";
				field.Label = "User Name";
				field.PlaceholderText = "";
				field.Description = "screen name for the user";
				field.HelpText = "";
				field.Required = true;
				field.Unique = true;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = string.Empty;
				field.MaxLength = 200;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: username. " + resp.Message);
			}

			// email field (ERPService.cs lines 179-200)
			{
				var field = new InputEmailField();
				field.Id = new Guid("9FC75C8F-CE80-4A64-81D7-E2BEFA5E4815");
				field.Name = "email";
				field.Label = "Email";
				field.PlaceholderText = "";
				field.Description = "Email address of the user";
				field.HelpText = "";
				field.Required = true;
				field.Unique = true;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = string.Empty;
				field.MaxLength = 255;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: email. " + resp.Message);
			}

			// password field (ERPService.cs lines 202-224)
			{
				var field = new InputPasswordField();
				field.Id = new Guid("4EDE88D9-217A-4462-9300-EA0D6AFCDCEA");
				field.Name = "password";
				field.Label = "Password";
				field.PlaceholderText = "";
				field.Description = "Password for the user account";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.MinLength = 6;
				field.MaxLength = 24;
				field.Encrypted = true;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: password. " + resp.Message);
			}

			// last_logged_in field (ERPService.cs lines 226-248)
			{
				var field = new InputDateTimeField();
				field.Id = new Guid("3C85CCEC-D526-4E47-887F-EE169D1F508D");
				field.Name = "last_logged_in";
				field.Label = "Last Logged In";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = true;
				field.System = true;
				field.DefaultValue = null;
				field.Format = "dd MMM yyyy HH:mm:ss";
				field.UseCurrentTimeAsDefaultValue = true;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: last_logged_in. " + resp.Message);
			}

			// enabled field (ERPService.cs lines 250-270)
			{
				var field = new InputCheckboxField();
				field.Id = new Guid("C0C63650-7572-4252-8E4B-4E25C94897A6");
				field.Name = "enabled";
				field.Label = "Enabled";
				field.PlaceholderText = "";
				field.Description = "Shows if the user account is enabled";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = false;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: enabled. " + resp.Message);
			}

			// verified field (ERPService.cs lines 272-293)
			{
				var field = new InputCheckboxField();
				field.Id = new Guid("F1BA5069-8CC9-4E66-BCC3-60E33C79C265");
				field.Name = "verified";
				field.Label = "Verified";
				field.PlaceholderText = "";
				field.Description = "Shows if the user email is verified";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = false;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: verified. " + resp.Message);
			}

			// preferences field (ERPService.cs lines 295-316)
			{
				var field = new InputTextField();
				field.Id = new Guid("29d46dac-b477-48f8-9f3a-22d7e95ae1cc");
				field.Name = "preferences";
				field.Label = "Preferences";
				field.PlaceholderText = "";
				field.Description = "Preferences json field.";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "{}";
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: preferences. " + resp.Message);
			}

			// image field (ERPService.cs lines 318-339)
			{
				var field = new InputImageField();
				field.Id = new Guid("bf199b74-4448-4f58-93f5-6b86d888843b");
				field.Name = "image";
				field.Label = "Image";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = string.Empty;
				field.EnableSecurity = false;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user. Field: image. " + resp.Message);
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Creates the role entity with 2 fields matching ERPService.cs lines 344-417.
		/// Entity ID: <c>c4541fee-fbb6-4661-929e-1724adec285a</c> (SystemIds.RoleEntityId).
		/// </summary>
		private async Task SeedRoleEntityAsync(EntityManager entMan)
		{
			// Idempotency check
			var existingEntity = entMan.ReadEntity(SystemIds.RoleEntityId);
			if (existingEntity.Object != null)
				return;

			// Create role entity (ERPService.cs lines 344-370)
			var roleEntity = new InputEntity();
			roleEntity.Id = SystemIds.RoleEntityId;
			roleEntity.Name = "role";
			roleEntity.Label = "Role";
			roleEntity.LabelPlural = "Roles";
			roleEntity.System = true;
			roleEntity.Color = "#f44336";
			roleEntity.IconName = "fa fa-key";
			roleEntity.RecordPermissions = new RecordPermissions();
			roleEntity.RecordPermissions.CanCreate = new List<Guid>();
			roleEntity.RecordPermissions.CanRead = new List<Guid>();
			roleEntity.RecordPermissions.CanUpdate = new List<Guid>();
			roleEntity.RecordPermissions.CanDelete = new List<Guid>();
			roleEntity.RecordPermissions.CanCreate.Add(SystemIds.GuestRoleId);
			roleEntity.RecordPermissions.CanCreate.Add(SystemIds.AdministratorRoleId);
			roleEntity.RecordPermissions.CanRead.Add(SystemIds.RegularRoleId);
			roleEntity.RecordPermissions.CanRead.Add(SystemIds.GuestRoleId);
			roleEntity.RecordPermissions.CanRead.Add(SystemIds.AdministratorRoleId);
			roleEntity.RecordPermissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
			roleEntity.RecordPermissions.CanDelete.Add(SystemIds.AdministratorRoleId);

			var createResponse = entMan.CreateEntity(roleEntity, true, false);
			if (!createResponse.Success)
				throw new Exception("Failed to create role entity: " + createResponse.Message);

			var entityId = SystemIds.RoleEntityId;

			// name field (ERPService.cs lines 372-397)
			{
				var field = new InputTextField();
				field.Id = new Guid("36F91EBD-5A02-4032-8498-B7F716F6A349");
				field.Name = "name";
				field.Label = "Name";
				field.PlaceholderText = "";
				field.Description = "The name of the role";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "";
				field.MaxLength = 200;
				field.EnableSecurity = true;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				field.Permissions.CanRead.Add(SystemIds.AdministratorRoleId);
				field.Permissions.CanRead.Add(SystemIds.RegularRoleId);
				field.Permissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: role. Field: name. " + resp.Message);
			}

			// description field (ERPService.cs lines 399-416)
			{
				var field = new InputTextField();
				field.Id = new Guid("4A8B9E0A-1C36-40C6-972B-B19E2B5D265B");
				field.Name = "description";
				field.Label = "Description";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "";
				field.MaxLength = 200;
				var resp = entMan.CreateField(entityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: role. Field: description. " + resp.Message);
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Creates the user_file entity with all fields matching ERPService.cs lines 531-862.
		/// Entity ID: <c>5c666c54-9e76-4327-ac7a-55851037810c</c>.
		/// </summary>
		private async Task SeedUserFileEntityAsync(EntityManager entMan)
		{
			var userFileEntityId = new Guid("5c666c54-9e76-4327-ac7a-55851037810c");

			// Idempotency check
			var existingEntity = entMan.ReadEntity(userFileEntityId);
			if (existingEntity.Object != null)
				return;

			// Create user_file entity (ERPService.cs lines 531-571)
			var entity = new InputEntity();
			entity.Id = userFileEntityId;
			entity.Name = "user_file";
			entity.Label = "User File";
			entity.LabelPlural = "User Files";
			entity.System = true;
			entity.IconName = "fa fa-file";
			entity.Color = "#f44336";
			entity.RecordPermissions = new RecordPermissions();
			entity.RecordPermissions.CanCreate = new List<Guid>();
			entity.RecordPermissions.CanRead = new List<Guid>();
			entity.RecordPermissions.CanUpdate = new List<Guid>();
			entity.RecordPermissions.CanDelete = new List<Guid>();
			// Admin + Regular for all CRUD (ERPService.cs lines 556-566)
			entity.RecordPermissions.CanCreate.Add(SystemIds.AdministratorRoleId);
			entity.RecordPermissions.CanCreate.Add(SystemIds.RegularRoleId);
			entity.RecordPermissions.CanRead.Add(SystemIds.AdministratorRoleId);
			entity.RecordPermissions.CanRead.Add(SystemIds.RegularRoleId);
			entity.RecordPermissions.CanUpdate.Add(SystemIds.AdministratorRoleId);
			entity.RecordPermissions.CanUpdate.Add(SystemIds.RegularRoleId);
			entity.RecordPermissions.CanDelete.Add(SystemIds.AdministratorRoleId);
			entity.RecordPermissions.CanDelete.Add(SystemIds.RegularRoleId);

			var createResponse = entMan.CreateEntity(entity, true, false);
			if (!createResponse.Success)
				throw new Exception("Failed to create user_file entity: " + createResponse.Message);

			// created_on field (ERPService.cs lines 577-607)
			{
				var field = new InputDateTimeField();
				field.Id = new Guid("7bc7c1a2-93aa-40bc-8374-fd350cdc7fac");
				field.Name = "created_on";
				field.Label = "Created On";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = true;
				field.System = true;
				field.DefaultValue = null;
				field.Format = "dd MMM yyyy HH:mm:ss";
				field.UseCurrentTimeAsDefaultValue = true;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: created_on. " + resp.Message);
			}

			// alt field (ERPService.cs lines 610-637)
			{
				var field = new InputTextField();
				field.Id = new Guid("168a9777-a156-4b0b-9b18-909fec043ce5");
				field.Name = "alt";
				field.Label = "Alt";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = null;
				field.MaxLength = null;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: alt. " + resp.Message);
			}

			// caption field (ERPService.cs lines 640-667)
			{
				var field = new InputTextField();
				field.Id = new Guid("6796c578-22f4-4b07-8568-99f9d6600294");
				field.Name = "caption";
				field.Label = "Caption";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = null;
				field.MaxLength = null;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: caption. " + resp.Message);
			}

			// height field (ERPService.cs lines 670-699)
			{
				var field = new InputNumberField();
				field.Id = new Guid("a7a06f28-5893-4890-a8a7-fd794c741cf9");
				field.Name = "height";
				field.Label = "Height";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = 0.0m;
				field.MinValue = null;
				field.MaxValue = null;
				field.DecimalPlaces = 0;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: height. " + resp.Message);
			}

			// name field (ERPService.cs lines 702-729)
			{
				var field = new InputTextField();
				field.Id = new Guid("cc2730d3-7711-4d8a-bdc2-1d11c3eae5c2");
				field.Name = "name";
				field.Label = "Name";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "file-name";
				field.MaxLength = null;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: name. " + resp.Message);
			}

			// size field (ERPService.cs lines 732-761)
			{
				var field = new InputNumberField();
				field.Id = new Guid("6a66fbd8-fb5a-4e48-882f-b760475bf2f0");
				field.Name = "size";
				field.Label = "Size";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = 0.0m;
				field.MinValue = null;
				field.MaxValue = null;
				field.DecimalPlaces = 0;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: size. " + resp.Message);
			}

			// type field (ERPService.cs lines 764-798)
			{
				var field = new InputSelectField();
				field.Id = new Guid("e856b229-ab8c-440c-8b6d-f817cc2776f0");
				field.Name = "type";
				field.Label = "Type";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = "image";
				field.Options = new List<SelectOption>
				{
					new SelectOption() { Value = "image", Label = "image" },
					new SelectOption() { Value = "document", Label = "document" },
					new SelectOption() { Value = "audio", Label = "audio" },
					new SelectOption() { Value = "video", Label = "video" },
					new SelectOption() { Value = "other", Label = "other" }
				};
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: type. " + resp.Message);
			}

			// width field (ERPService.cs lines 801-831)
			{
				var field = new InputNumberField();
				field.Id = new Guid("c2b8fee6-81a4-4cb0-adac-f19f734f6380");
				field.Name = "width";
				field.Label = "Width";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = true;
				field.DefaultValue = 0.0m;
				field.MinValue = null;
				field.MaxValue = null;
				field.DecimalPlaces = 0;
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: width. " + resp.Message);
			}

			// path field (ERPService.cs lines 833-860)
			{
				var field = new InputFileField();
				field.Id = new Guid("3f4e8056-6e94-4304-8fd7-8f151c81bc19");
				field.Name = "path";
				field.Label = "File";
				field.PlaceholderText = "";
				field.Description = "";
				field.HelpText = "";
				field.Required = true;
				field.Unique = true;
				field.Searchable = true;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = "no-file-error.txt";
				field.EnableSecurity = false;
				field.Permissions = new FieldPermissions();
				field.Permissions.CanRead = new List<Guid>();
				field.Permissions.CanUpdate = new List<Guid>();
				var resp = entMan.CreateField(userFileEntityId, field, false);
				if (!resp.Success)
					throw new Exception("System error 10060. Entity: user_file. Field: path. " + resp.Message);
			}

			await Task.CompletedTask;
		}

		#endregion

		#region << SeedDefaultUsersAndRolesAsync >>

		/// <summary>
		/// Creates the default system user, first admin user, and three roles
		/// (administrator, regular, guest) matching ERPService.cs lines 444-509.
		/// Idempotent: uses try-catch to silently skip duplicates.
		/// </summary>
		private async Task SeedDefaultUsersAndRolesAsync()
		{
			var recMan = _serviceProvider.GetRequiredService<RecordManager>();

			// System user (ERPService.cs lines 447-459)
			try
			{
				var systemUser = new EntityRecord();
				systemUser["id"] = SystemIds.SystemUserId;
				systemUser["first_name"] = "Local";
				systemUser["last_name"] = "System";
				systemUser["password"] = Guid.NewGuid().ToString();
				systemUser["email"] = "system@webvella.com";
				systemUser["username"] = "system";
				systemUser["created_on"] = new DateTime(2010, 10, 10);
				systemUser["enabled"] = true;
				var result = recMan.CreateRecord("user", systemUser);
				if (!result.Success)
					throw new Exception("CREATE SYSTEM USER RECORD: " + result.Message);
			}
			catch (Exception)
			{
				// Idempotent: record may already exist
			}

			// First/Admin user (ERPService.cs lines 462-476)
			try
			{
				var firstUser = new EntityRecord();
				firstUser["id"] = SystemIds.FirstUserId;
				firstUser["first_name"] = "WebVella";
				firstUser["last_name"] = "Erp";
				firstUser["password"] = "erp";
				firstUser["email"] = "erp@webvella.com";
				firstUser["username"] = "administrator";
				firstUser["created_on"] = new DateTime(2010, 10, 10);
				firstUser["enabled"] = true;
				var result = recMan.CreateRecord("user", firstUser);
				if (!result.Success)
					throw new Exception("CREATE FIRST USER RECORD: " + result.Message);
			}
			catch (Exception)
			{
				// Idempotent: record may already exist
			}

			// Administrator role (ERPService.cs lines 478-487)
			try
			{
				var adminRole = new EntityRecord();
				adminRole["id"] = SystemIds.AdministratorRoleId;
				adminRole["name"] = "administrator";
				adminRole["description"] = "";
				var result = recMan.CreateRecord("role", adminRole);
				if (!result.Success)
					throw new Exception("CREATE ADMINISTRATOR ROLE RECORD: " + result.Message);
			}
			catch (Exception)
			{
				// Idempotent: record may already exist
			}

			// Regular role (ERPService.cs lines 489-498)
			try
			{
				var regularRole = new EntityRecord();
				regularRole["id"] = SystemIds.RegularRoleId;
				regularRole["name"] = "regular";
				regularRole["description"] = "";
				var result = recMan.CreateRecord("role", regularRole);
				if (!result.Success)
					throw new Exception("CREATE REGULAR ROLE RECORD: " + result.Message);
			}
			catch (Exception)
			{
				// Idempotent: record may already exist
			}

			// Guest role (ERPService.cs lines 500-509)
			try
			{
				var guestRole = new EntityRecord();
				guestRole["id"] = SystemIds.GuestRoleId;
				guestRole["name"] = "guest";
				guestRole["description"] = "";
				var result = recMan.CreateRecord("role", guestRole);
				if (!result.Success)
					throw new Exception("CREATE GUEST ROLE RECORD: " + result.Message);
			}
			catch (Exception)
			{
				// Idempotent: record may already exist
			}

			await Task.CompletedTask;
		}

		#endregion

		#region << SeedUserRoleRelationsAsync >>

		/// <summary>
		/// Creates the user-role ManyToMany relation and links users to roles
		/// matching ERPService.cs lines 421-527.
		/// </summary>
		private async Task SeedUserRoleRelationsAsync()
		{
			var entMan = _serviceProvider.GetRequiredService<EntityManager>();
			var relMan = _serviceProvider.GetRequiredService<EntityRelationManager>();
			var recMan = _serviceProvider.GetRequiredService<RecordManager>();

			// Create user-role relation (ERPService.cs lines 421-442)
			try
			{
				var userEntity = entMan.ReadEntity(SystemIds.UserEntityId).Object;
				var roleEntity = entMan.ReadEntity(SystemIds.RoleEntityId).Object;

				if (userEntity != null && roleEntity != null)
				{
					var existingRelation = relMan.Read(SystemIds.UserRoleRelationId);
					if (existingRelation == null || existingRelation.Object == null)
					{
						var userRoleRelation = new EntityRelation();
						userRoleRelation.Id = SystemIds.UserRoleRelationId;
						userRoleRelation.Name = "user_role";
						userRoleRelation.Label = "User-Role";
						userRoleRelation.System = true;
						userRoleRelation.RelationType = EntityRelationType.ManyToMany;
						userRoleRelation.TargetEntityId = userEntity.Id;
						userRoleRelation.TargetFieldId = FindIdFieldId(userEntity);
						userRoleRelation.OriginEntityId = roleEntity.Id;
						userRoleRelation.OriginFieldId = FindIdFieldId(roleEntity);

						var result = relMan.Create(userRoleRelation);
						if (!result.Success)
							throw new Exception("CREATE USER-ROLE RELATION: " + result.Message);
					}
				}
			}
			catch (Exception)
			{
				// Idempotent: relation may already exist
			}

			// System user <-> Administrator role (ERPService.cs line 512)
			try
			{
				recMan.CreateRelationManyToManyRecord(
					SystemIds.UserRoleRelationId,
					SystemIds.AdministratorRoleId,
					SystemIds.SystemUserId);
			}
			catch (Exception)
			{
				// Idempotent: relation record may already exist
			}

			// First user <-> Administrator role (ERPService.cs line 518)
			try
			{
				recMan.CreateRelationManyToManyRecord(
					SystemIds.UserRoleRelationId,
					SystemIds.AdministratorRoleId,
					SystemIds.FirstUserId);
			}
			catch (Exception)
			{
				// Idempotent: relation record may already exist
			}

			// First user <-> Regular role (ERPService.cs line 523)
			try
			{
				recMan.CreateRelationManyToManyRecord(
					SystemIds.UserRoleRelationId,
					SystemIds.RegularRoleId,
					SystemIds.FirstUserId);
			}
			catch (Exception)
			{
				// Idempotent: relation record may already exist
			}

			await Task.CompletedTask;
		}

		#endregion

		#region << SeedSampleEntitiesAsync >>

		// Well-known GUIDs for test entities (generated for test purposes)
		private static readonly Guid TestEntity1Id = new Guid("a1b2c3d4-e5f6-7890-abcd-ef0123456789");
		private static readonly Guid TestEntity2Id = new Guid("b2c3d4e5-f6a7-8901-bcde-f01234567890");

		/// <summary>
		/// Creates additional test entities with various field types for
		/// comprehensive EntityManager CRUD testing. These entities are NOT
		/// from the monolith — they are test-specific sample data.
		/// </summary>
		private async Task SeedSampleEntitiesAsync()
		{
			var entMan = _serviceProvider.GetRequiredService<EntityManager>();

			// test_entity — diverse field types for field-type coverage testing
			await SeedTestEntity1Async(entMan);

			// test_entity_2 — second entity for relation testing
			await SeedTestEntity2Async(entMan);
		}

		/// <summary>
		/// Creates test_entity with TextField, NumberField, DateTimeField,
		/// CheckboxField, MultiSelectField, and EmailField for comprehensive
		/// field-type testing.
		/// </summary>
		private async Task SeedTestEntity1Async(EntityManager entMan)
		{
			// Idempotency check
			var existingEntity = entMan.ReadEntity("test_entity");
			if (existingEntity.Object != null)
				return;

			var testEntity = new InputEntity();
			testEntity.Id = TestEntity1Id;
			testEntity.Name = "test_entity";
			testEntity.Label = "Test Entity";
			testEntity.LabelPlural = "Test Entities";
			testEntity.System = false;
			testEntity.IconName = "fa fa-flask";
			testEntity.Color = "#2196F3";
			testEntity.RecordPermissions = new RecordPermissions();
			testEntity.RecordPermissions.CanCreate = new List<Guid> { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId };
			testEntity.RecordPermissions.CanRead = new List<Guid> { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId, SystemIds.GuestRoleId };
			testEntity.RecordPermissions.CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId };
			testEntity.RecordPermissions.CanDelete = new List<Guid> { SystemIds.AdministratorRoleId };

			var createResponse = entMan.CreateEntity(testEntity, true, false);
			if (!createResponse.Success)
				throw new Exception("Failed to create test_entity: " + createResponse.Message);

			// TextField
			{
				var field = new InputTextField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456001");
				field.Name = "test_text";
				field.Label = "Test Text";
				field.PlaceholderText = "Enter text";
				field.Description = "A test text field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = "";
				field.MaxLength = 500;
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_text field: " + resp.Message);
			}

			// NumberField
			{
				var field = new InputNumberField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456002");
				field.Name = "test_number";
				field.Label = "Test Number";
				field.PlaceholderText = "";
				field.Description = "A test number field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = 0.0m;
				field.MinValue = null;
				field.MaxValue = null;
				field.DecimalPlaces = 2;
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_number field: " + resp.Message);
			}

			// DateTimeField
			{
				var field = new InputDateTimeField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456003");
				field.Name = "test_datetime";
				field.Label = "Test DateTime";
				field.PlaceholderText = "";
				field.Description = "A test datetime field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = null;
				field.Format = "dd MMM yyyy HH:mm:ss";
				field.UseCurrentTimeAsDefaultValue = false;
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_datetime field: " + resp.Message);
			}

			// CheckboxField
			{
				var field = new InputCheckboxField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456004");
				field.Name = "test_checkbox";
				field.Label = "Test Checkbox";
				field.PlaceholderText = "";
				field.Description = "A test checkbox field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = false;
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_checkbox field: " + resp.Message);
			}

			// MultiSelectField
			{
				var field = new InputMultiSelectField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456005");
				field.Name = "test_multiselect";
				field.Label = "Test MultiSelect";
				field.PlaceholderText = "";
				field.Description = "A test multi-select field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = new List<string>();
				field.Options = new List<SelectOption>
				{
					new SelectOption("option1", "Option 1"),
					new SelectOption("option2", "Option 2"),
					new SelectOption("option3", "Option 3")
				};
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_multiselect field: " + resp.Message);
			}

			// EmailField
			{
				var field = new InputEmailField();
				field.Id = new Guid("d1e2f3a4-b5c6-7890-abcd-ef0123456006");
				field.Name = "test_email";
				field.Label = "Test Email";
				field.PlaceholderText = "";
				field.Description = "A test email field";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = "";
				field.MaxLength = 255;
				var resp = entMan.CreateField(TestEntity1Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_email field: " + resp.Message);
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Creates test_entity_2 for relation testing between test entities.
		/// Contains a GuidField for OneToMany foreign key reference.
		/// </summary>
		private async Task SeedTestEntity2Async(EntityManager entMan)
		{
			// Idempotency check
			var existingEntity = entMan.ReadEntity("test_entity_2");
			if (existingEntity.Object != null)
				return;

			var testEntity2 = new InputEntity();
			testEntity2.Id = TestEntity2Id;
			testEntity2.Name = "test_entity_2";
			testEntity2.Label = "Test Entity 2";
			testEntity2.LabelPlural = "Test Entities 2";
			testEntity2.System = false;
			testEntity2.IconName = "fa fa-flask";
			testEntity2.Color = "#4CAF50";
			testEntity2.RecordPermissions = new RecordPermissions();
			testEntity2.RecordPermissions.CanCreate = new List<Guid> { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId };
			testEntity2.RecordPermissions.CanRead = new List<Guid> { SystemIds.AdministratorRoleId, SystemIds.RegularRoleId, SystemIds.GuestRoleId };
			testEntity2.RecordPermissions.CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId };
			testEntity2.RecordPermissions.CanDelete = new List<Guid> { SystemIds.AdministratorRoleId };

			var createResponse = entMan.CreateEntity(testEntity2, true, false);
			if (!createResponse.Success)
				throw new Exception("Failed to create test_entity_2: " + createResponse.Message);

			// TextField for testing
			{
				var field = new InputTextField();
				field.Id = new Guid("e2f3a4b5-c6d7-8901-bcde-f01234567001");
				field.Name = "name";
				field.Label = "Name";
				field.PlaceholderText = "";
				field.Description = "Name field for test entity 2";
				field.HelpText = "";
				field.Required = true;
				field.Unique = false;
				field.Searchable = true;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = "";
				field.MaxLength = 200;
				var resp = entMan.CreateField(TestEntity2Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create name field on test_entity_2: " + resp.Message);
			}

			// GuidField — foreign key for OneToMany relation to test_entity
			{
				var field = new InputGuidField();
				field.Id = new Guid("e2f3a4b5-c6d7-8901-bcde-f01234567002");
				field.Name = "test_entity_id";
				field.Label = "Test Entity Id";
				field.PlaceholderText = "";
				field.Description = "FK reference to test_entity";
				field.HelpText = "";
				field.Required = false;
				field.Unique = false;
				field.Searchable = false;
				field.Auditable = false;
				field.System = false;
				field.DefaultValue = null;
				field.GenerateNewId = false;
				var resp = entMan.CreateField(TestEntity2Id, field, false);
				if (!resp.Success)
					throw new Exception("Failed to create test_entity_id field on test_entity_2: " + resp.Message);
			}

			await Task.CompletedTask;
		}

		#endregion

		#region << SeedSampleRelationsAsync >>

		// Well-known GUIDs for test relations
		private static readonly Guid TestOneToManyRelationId = new Guid("c3d4e5f6-a7b8-9012-cdef-012345678901");
		private static readonly Guid TestManyToManyRelationId = new Guid("c3d4e5f6-a7b8-9012-cdef-012345678902");

		/// <summary>
		/// Creates sample entity relations between test entities for testing
		/// EntityRelationManager and EQL relation query capabilities.
		/// </summary>
		private async Task SeedSampleRelationsAsync()
		{
			var entMan = _serviceProvider.GetRequiredService<EntityManager>();
			var relMan = _serviceProvider.GetRequiredService<EntityRelationManager>();

			// OneToMany relation: test_entity (1) -> test_entity_2 (N)
			try
			{
				var existingRelation = relMan.Read(TestOneToManyRelationId);
				if (existingRelation == null || existingRelation.Object == null)
				{
					var testEntity1 = entMan.ReadEntity(TestEntity1Id).Object;
					var testEntity2 = entMan.ReadEntity(TestEntity2Id).Object;

					if (testEntity1 != null && testEntity2 != null)
					{
						var oneToManyRelation = new EntityRelation();
						oneToManyRelation.Id = TestOneToManyRelationId;
						oneToManyRelation.Name = "test_entity_test_entity_2";
						oneToManyRelation.Label = "Test Entity -> Test Entity 2";
						oneToManyRelation.System = false;
						oneToManyRelation.RelationType = EntityRelationType.OneToMany;
						oneToManyRelation.OriginEntityId = testEntity1.Id;
						oneToManyRelation.OriginFieldId = FindIdFieldId(testEntity1);
						oneToManyRelation.TargetEntityId = testEntity2.Id;
						oneToManyRelation.TargetFieldId = FindFieldIdByName(testEntity2, "test_entity_id");

						var result = relMan.Create(oneToManyRelation);
						if (!result.Success)
							throw new Exception("CREATE TEST ONE-TO-MANY RELATION: " + result.Message);
					}
				}
			}
			catch (Exception)
			{
				// Idempotent: relation may already exist
			}

			// ManyToMany relation: test_entity <-> user (for testing cross-entity M:N)
			try
			{
				var existingRelation = relMan.Read(TestManyToManyRelationId);
				if (existingRelation == null || existingRelation.Object == null)
				{
					var testEntity1 = entMan.ReadEntity(TestEntity1Id).Object;
					var userEntity = entMan.ReadEntity(SystemIds.UserEntityId).Object;

					if (testEntity1 != null && userEntity != null)
					{
						var manyToManyRelation = new EntityRelation();
						manyToManyRelation.Id = TestManyToManyRelationId;
						manyToManyRelation.Name = "test_entity_user";
						manyToManyRelation.Label = "Test Entity <-> User";
						manyToManyRelation.System = false;
						manyToManyRelation.RelationType = EntityRelationType.ManyToMany;
						manyToManyRelation.OriginEntityId = testEntity1.Id;
						manyToManyRelation.OriginFieldId = FindIdFieldId(testEntity1);
						manyToManyRelation.TargetEntityId = userEntity.Id;
						manyToManyRelation.TargetFieldId = FindIdFieldId(userEntity);

						var result = relMan.Create(manyToManyRelation);
						if (!result.Success)
							throw new Exception("CREATE TEST MANY-TO-MANY RELATION: " + result.Message);
					}
				}
			}
			catch (Exception)
			{
				// Idempotent: relation may already exist
			}

			await Task.CompletedTask;
		}

		#endregion

		#region << Helper Methods >>

		/// <summary>
		/// Executes raw SQL against the test database using a direct NpgsqlConnection.
		/// Used for database schema setup (extensions, system tables) that bypasses
		/// the ERP manager layer.
		/// </summary>
		/// <param name="sql">The SQL statement to execute.</param>
		private async Task ExecuteSqlAsync(string sql)
		{
			var connectionString = GetConnectionString();
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync();
			using var command = new NpgsqlCommand(sql, connection);
			command.CommandTimeout = 60;
			await command.ExecuteNonQueryAsync();
		}

		/// <summary>
		/// Executes parameterized raw SQL against the test database.
		/// </summary>
		/// <param name="sql">The SQL statement to execute.</param>
		/// <param name="parameters">The SQL parameters to bind.</param>
		private async Task ExecuteSqlAsync(string sql, NpgsqlParameter[] parameters)
		{
			var connectionString = GetConnectionString();
			using var connection = new NpgsqlConnection(connectionString);
			await connection.OpenAsync();
			using var command = new NpgsqlCommand(sql, connection);
			command.CommandTimeout = 60;
			if (parameters != null)
			{
				command.Parameters.AddRange(parameters);
			}
			await command.ExecuteNonQueryAsync();
		}

		/// <summary>
		/// Resolves the PostgreSQL connection string from the test DI container's
		/// <see cref="IConfiguration"/>. Reads <c>ConnectionStrings:Default</c>.
		/// </summary>
		/// <returns>The PostgreSQL connection string for the test database.</returns>
		private string GetConnectionString()
		{
			var configuration = _serviceProvider.GetRequiredService<IConfiguration>();
			var connectionString = configuration["ConnectionStrings:Default"];
			if (string.IsNullOrWhiteSpace(connectionString))
				throw new InvalidOperationException(
					"ConnectionStrings:Default is not configured. Ensure the test fixture provides a valid PostgreSQL connection string.");
			return connectionString;
		}

		/// <summary>
		/// Finds the GUID of the 'id' field in an entity's field collection.
		/// Used when creating entity relations that reference the primary key field.
		/// </summary>
		/// <param name="entity">The entity whose 'id' field to locate.</param>
		/// <returns>The GUID of the 'id' field.</returns>
		private static Guid FindIdFieldId(Entity entity)
		{
			if (entity == null || entity.Fields == null)
				throw new InvalidOperationException("Entity or its fields collection is null.");

			foreach (var field in entity.Fields)
			{
				if (string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase))
					return field.Id;
			}

			throw new InvalidOperationException($"Entity '{entity.Name}' does not have an 'id' field.");
		}

		/// <summary>
		/// Finds the GUID of a named field in an entity's field collection.
		/// Used when creating entity relations that reference non-primary-key fields.
		/// </summary>
		/// <param name="entity">The entity whose field to locate.</param>
		/// <param name="fieldName">The name of the field to find.</param>
		/// <returns>The GUID of the matching field.</returns>
		private static Guid FindFieldIdByName(Entity entity, string fieldName)
		{
			if (entity == null || entity.Fields == null)
				throw new InvalidOperationException("Entity or its fields collection is null.");

			foreach (var field in entity.Fields)
			{
				if (string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase))
					return field.Id;
			}

			throw new InvalidOperationException($"Entity '{entity.Name}' does not have a field named '{fieldName}'.");
		}

		#endregion
	}
}
