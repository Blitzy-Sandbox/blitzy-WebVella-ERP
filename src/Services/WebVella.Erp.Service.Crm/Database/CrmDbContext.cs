// =============================================================================
// CrmDbContext.cs — CRM Microservice Database Context
// =============================================================================
// CRM-specific EF Core DbContext for the independently deployable CRM
// microservice, implementing the database-per-service pattern with its own
// PostgreSQL database (erp_crm). Manages CRM-owned entities: account, contact,
// case, address, and salutation (table: rec_solutation — typo preserved from
// the original monolith source NextPlugin.20190203.cs).
//
// Architecture:
//   - Inherits Microsoft.EntityFrameworkCore.DbContext for EF Core migrations
//     and entity configuration.
//   - Implements IDbContext from SharedKernel for interoperability with
//     SharedKernel's static DbRepository DDL/DML helpers when needed
//     (e.g., during migration tooling or direct SQL execution).
//   - Preserves the monolith's ambient context pattern (AsyncLocal + 
//     ConcurrentDictionary) via static CreateContext/CloseContext factory
//     methods for backward compatibility with SharedKernel utilities.
//
// Source references:
//   - WebVella.Erp/Database/DbContext.cs (monolith ambient context pattern)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (entity creation)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (fields + relations)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (schema updates)
//   - WebVella.Erp.Plugins.Crm/CrmPlugin._.cs (CRM patch framework)
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Crm.Database
{
	/// <summary>
	/// CRM-specific EF Core DbContext for the CRM microservice.
	/// Manages CRM-owned entities (account, contact, case, address, salutation)
	/// and their relationships in the <c>erp_crm</c> database.
	/// Also implements <see cref="IDbContext"/> for SharedKernel database utility compatibility.
	/// </summary>
	// Suppress nullable reference warnings for the ambient context pattern that intentionally
	// sets AsyncLocal<string>.Value and DbContextAccessor.Current to null during cleanup.
#pragma warning disable CS8602, CS8625
	public class CrmDbContext : Microsoft.EntityFrameworkCore.DbContext, IDbContext
	{
		#region ===== Nested Entity Record Types =====

		/// <summary>
		/// EF Core entity mapping for the <c>rec_account</c> table.
		/// Entity ID: 2e22b50f-e444-4b62-a171-076e51246939 (from NextPlugin.20190203.cs).
		/// Fields cumulative from patches 20190203, 20190204, and 20190206.
		/// </summary>
		public class AccountRecord
		{
			public Guid Id { get; set; }
			public string? Name { get; set; }
			public string? LScope { get; set; }
			public string? Type { get; set; }
			public string? Website { get; set; }
			public string? Street { get; set; }
			public string? Street2 { get; set; }
			public string? Region { get; set; }
			public string? PostCode { get; set; }
			public string? FixedPhone { get; set; }
			public string? MobilePhone { get; set; }
			public string? FaxPhone { get; set; }
			public string? Notes { get; set; }
			public string? LastName { get; set; }
			public string? FirstName { get; set; }
			public string? XSearch { get; set; }
			public string? Email { get; set; }
			public string? City { get; set; }
			public string? TaxId { get; set; }
			// Cross-service references: stored as UUID, resolved via Core gRPC — no FK constraints
			public Guid? CountryId { get; set; }
			public Guid? LanguageId { get; set; }
			public Guid? CurrencyId { get; set; }
			// Salutation reference (within CRM, salutation_id from Patch20190206)
			public Guid? SalutationId { get; set; }
			// Audit fields
			public DateTime? CreatedOn { get; set; }
			public Guid? CreatedBy { get; set; }
			public DateTime? LastModifiedOn { get; set; }
			public Guid? LastModifiedBy { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_contact</c> table.
		/// Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0 (from NextPlugin.20190204.cs).
		/// </summary>
		public class ContactRecord
		{
			public Guid Id { get; set; }
			public string? Email { get; set; }
			public string? JobTitle { get; set; }
			public string? FirstName { get; set; }
			public string? LastName { get; set; }
			public string? Notes { get; set; }
			public string? FixedPhone { get; set; }
			public string? MobilePhone { get; set; }
			public string? FaxPhone { get; set; }
			public string? City { get; set; }
			public string? Region { get; set; }
			public string? Street { get; set; }
			public string? Street2 { get; set; }
			public string? PostCode { get; set; }
			public string? XSearch { get; set; }
			public string? LScope { get; set; }
			public string? Photo { get; set; }
			// Cross-service reference: country resolved via Core gRPC — no FK constraint
			public Guid? CountryId { get; set; }
			// Audit fields
			public DateTime? CreatedOn { get; set; }
			public Guid? CreatedBy { get; set; }
			public DateTime? LastModifiedOn { get; set; }
			public Guid? LastModifiedBy { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_case</c> table.
		/// Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c (from NextPlugin.20190203.cs).
		/// </summary>
		public class CaseRecord
		{
			public Guid Id { get; set; }
			public string? Subject { get; set; }
			public string? Description { get; set; }
			public string? Priority { get; set; }
			public string? XSearch { get; set; }
			public string? LScope { get; set; }
			public decimal? Number { get; set; }
			public DateTime? ClosedOn { get; set; }
			// FK to case_status (within CRM boundary)
			public Guid? StatusId { get; set; }
			// FK to case_type (within CRM boundary)
			public Guid? TypeId { get; set; }
			// Cross-service reference: account resolved via denormalized UUID
			public Guid? AccountId { get; set; }
			// Cross-service reference: owner resolved via Core gRPC
			public Guid? OwnerId { get; set; }
			// Audit fields
			public DateTime? CreatedOn { get; set; }
			public Guid? CreatedBy { get; set; }
			public DateTime? LastModifiedOn { get; set; }
			public Guid? LastModifiedBy { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_address</c> table.
		/// Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0 (from NextPlugin.20190204.cs).
		/// </summary>
		public class AddressRecord
		{
			public Guid Id { get; set; }
			public string? Name { get; set; }
			public string? Street { get; set; }
			public string? Street2 { get; set; }
			public string? City { get; set; }
			public string? Region { get; set; }
			public string? Notes { get; set; }
			// Cross-service reference: country resolved via Core gRPC — no FK constraint
			public Guid? CountryId { get; set; }
			// Audit fields
			public DateTime? CreatedOn { get; set; }
			public Guid? CreatedBy { get; set; }
			public DateTime? LastModifiedOn { get; set; }
			public Guid? LastModifiedBy { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_solutation</c> table.
		/// Entity ID: f0b64034-e0f6-452e-b82b-88186af6df88 (from NextPlugin.20190203.cs).
		/// Note: Table name preserves the "solutation" typo from the original monolith source.
		/// The DbSet property is named "Salutations" for a clean API surface.
		/// </summary>
		public class SalutationRecord
		{
			public Guid Id { get; set; }
			public string? Label { get; set; }
			public bool? IsDefault { get; set; }
			public bool? IsEnabled { get; set; }
			public bool? IsSystem { get; set; }
			public string? LScope { get; set; }
			public decimal? SortIndex { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_case_status</c> table.
		/// Lookup entity for case status values, owned by CRM service.
		/// </summary>
		public class CaseStatusRecord
		{
			public Guid Id { get; set; }
			public string? Label { get; set; }
			public bool? IsDefault { get; set; }
			public bool? IsEnabled { get; set; }
			public bool? IsSystem { get; set; }
			public bool? IsClosed { get; set; }
			public string? LScope { get; set; }
			public string? IconClass { get; set; }
			public string? Color { get; set; }
			public decimal? SortIndex { get; set; }
		}

		/// <summary>
		/// EF Core entity mapping for the <c>rec_case_type</c> table.
		/// Lookup entity for case type values, owned by CRM service.
		/// </summary>
		public class CaseTypeRecord
		{
			public Guid Id { get; set; }
			public string? Label { get; set; }
			public bool? IsDefault { get; set; }
			public bool? IsEnabled { get; set; }
			public bool? IsSystem { get; set; }
			public string? LScope { get; set; }
			public string? IconClass { get; set; }
			public string? Color { get; set; }
			public decimal? SortIndex { get; set; }
		}

		#endregion

		#region ===== Static Ambient Context Fields (Legacy Compatibility) =====

		/// <summary>
		/// AsyncLocal storage for the current CRM database context identifier,
		/// maintaining the ambient context ID per async execution context.
		/// Mirrors the monolith's DbContext pattern for SharedKernel utility compatibility.
		/// </summary>
		private static readonly AsyncLocal<string> currentDbContextId = new AsyncLocal<string>();

		/// <summary>
		/// Thread-safe dictionary mapping context IDs to CrmDbContext instances.
		/// Enables the static <see cref="Current"/> accessor pattern.
		/// </summary>
		private static readonly ConcurrentDictionary<string, CrmDbContext> dbContextDict =
			new ConcurrentDictionary<string, CrmDbContext>();

		/// <summary>
		/// Lock object for thread-safe connection stack operations in <see cref="CloseConnection"/>.
		/// </summary>
		private readonly object lockObj = new object();

		/// <summary>
		/// LIFO stack of open SharedKernel <see cref="WebVella.Erp.SharedKernel.Database.DbConnection"/>
		/// instances managed by this context for the IDbContext compatibility layer.
		/// </summary>
		private Stack<WebVella.Erp.SharedKernel.Database.DbConnection> connectionStack;

		/// <summary>
		/// Active Npgsql transaction for transactional state management.
		/// Set via <see cref="EnterTransactionalState"/> and cleared via <see cref="LeaveTransactionalState"/>.
		/// </summary>
		private NpgsqlTransaction? transaction;

		/// <summary>
		/// Connection string for the CRM PostgreSQL database, set by <see cref="CreateContext"/>
		/// or directly via the public <see cref="ConnectionString"/> property during service startup.
		/// Used by <see cref="CreateConnection"/> for the legacy DbConnection pattern (write operations).
		/// </summary>
		private static string? connectionString;

		/// <summary>
		/// Gets or sets the static connection string for the CRM database.
		/// Must be initialized during service startup so that <see cref="CreateConnection"/>
		/// can open connections for write operations (POST/PUT/DELETE) that use
		/// the legacy SharedKernel DbConnection pattern outside EF Core.
		/// </summary>
		public static string? ConnectionString
		{
			get => connectionString;
			set => connectionString = value;
		}

		#endregion

		#region ===== DbSet Properties =====

		/// <summary>
		/// CRM Account entities. Maps to <c>rec_account</c> table in the erp_crm database.
		/// Entity ID: 2e22b50f-e444-4b62-a171-076e51246939.
		/// </summary>
		public DbSet<AccountRecord> Accounts { get; set; } = null!;

		/// <summary>
		/// CRM Contact entities. Maps to <c>rec_contact</c> table in the erp_crm database.
		/// Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0.
		/// </summary>
		public DbSet<ContactRecord> Contacts { get; set; } = null!;

		/// <summary>
		/// CRM Case entities. Maps to <c>rec_case</c> table in the erp_crm database.
		/// Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c.
		/// </summary>
		public DbSet<CaseRecord> Cases { get; set; } = null!;

		/// <summary>
		/// CRM Address entities. Maps to <c>rec_address</c> table in the erp_crm database.
		/// Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0.
		/// </summary>
		public DbSet<AddressRecord> Addresses { get; set; } = null!;

		/// <summary>
		/// CRM Salutation entities. Maps to <c>rec_solutation</c> table (typo preserved from source).
		/// Entity ID: f0b64034-e0f6-452e-b82b-88186af6df88.
		/// </summary>
		public DbSet<SalutationRecord> Salutations { get; set; } = null!;

		#endregion

		#region ===== Constructors =====

		/// <summary>
		/// Standard EF Core constructor accepting options for DI-managed registration.
		/// Used when registered via <c>AddDbContext&lt;CrmDbContext&gt;()</c> in Program.cs.
		/// </summary>
		/// <param name="options">EF Core context configuration options with Npgsql provider.</param>
		public CrmDbContext(DbContextOptions<CrmDbContext> options) : base(options)
		{
			connectionStack = new Stack<WebVella.Erp.SharedKernel.Database.DbConnection>();
		}

		/// <summary>
		/// Parameterless constructor for static factory pattern and EF Core migration tooling.
		/// Used by <see cref="CreateContext"/> and <c>dotnet ef</c> design-time tools.
		/// </summary>
		private CrmDbContext() : base()
		{
			connectionStack = new Stack<WebVella.Erp.SharedKernel.Database.DbConnection>();
		}

		#endregion

		#region ===== Static Current Accessor =====

		/// <summary>
		/// Gets the current ambient CrmDbContext for the async execution context.
		/// Returns <c>null</c> if no context has been created via <see cref="CreateContext"/>.
		/// Mirrors the monolith's <c>DbContext.Current</c> singleton pattern, scoped to CRM service.
		/// </summary>
		public static CrmDbContext? Current
		{
			get
			{
				if (currentDbContextId == null || string.IsNullOrWhiteSpace(currentDbContextId.Value))
					return null;

				CrmDbContext? context = null;
				dbContextDict.TryGetValue(currentDbContextId.Value, out context);
				return context;
			}
		}

		#endregion

		#region ===== IDbContext Implementation (Legacy Compatibility Layer) =====

		/// <summary>
		/// Creates a new SharedKernel <see cref="WebVella.Erp.SharedKernel.Database.DbConnection"/>
		/// wrapping an Npgsql connection. If a transaction is active, the connection shares
		/// the transaction; otherwise a new connection is opened from the connection string.
		/// The connection is pushed onto the LIFO stack for lifecycle management.
		/// </summary>
		/// <returns>A new <see cref="WebVella.Erp.SharedKernel.Database.DbConnection"/> instance.</returns>
		public WebVella.Erp.SharedKernel.Database.DbConnection CreateConnection()
		{
			WebVella.Erp.SharedKernel.Database.DbConnection con;
			if (transaction != null)
				con = new WebVella.Erp.SharedKernel.Database.DbConnection(transaction, this);
			else
				con = new WebVella.Erp.SharedKernel.Database.DbConnection(connectionString!, this);

			connectionStack.Push(con);

			Debug.WriteLine($"CRM CreateConnection: {currentDbContextId.Value} | Stack count: {connectionStack.Count} | Hash: {con.GetHashCode()}");
			return con;
		}

		/// <summary>
		/// Closes the specified <see cref="WebVella.Erp.SharedKernel.Database.DbConnection"/>
		/// by popping it from the connection stack. Enforces LIFO ordering — attempting
		/// to close a connection before closing inner (later-opened) connections throws.
		/// Thread-safe via <c>lock (lockObj)</c> for concurrent access.
		/// </summary>
		/// <param name="conn">The connection to close. Must be the top of the stack.</param>
		/// <returns><c>true</c> if the connection stack is now empty; <c>false</c> otherwise.</returns>
		/// <exception cref="Exception">Thrown if <paramref name="conn"/> is not the topmost connection.</exception>
		public bool CloseConnection(WebVella.Erp.SharedKernel.Database.DbConnection conn)
		{
			lock (lockObj)
			{
				var dbConn = connectionStack.Peek();
				if (dbConn != conn)
					throw new Exception("You are trying to close connection, before closing inner connections.");

				connectionStack.Pop();

				Debug.WriteLine($"CRM CloseConnection: {currentDbContextId.Value} | Stack count: {connectionStack.Count} | Hash: {conn.GetHashCode()}");
				return connectionStack.Count == 0;
			}
		}

		/// <summary>
		/// Enters transactional state by storing the active <see cref="NpgsqlTransaction"/>.
		/// Subsequent <see cref="CreateConnection"/> calls will share this transaction.
		/// Called by SharedKernel's <see cref="WebVella.Erp.SharedKernel.Database.DbConnection.BeginTransaction"/>.
		/// </summary>
		/// <param name="transaction">The active Npgsql transaction to share across connections.</param>
		public void EnterTransactionalState(NpgsqlTransaction transaction)
		{
			this.transaction = transaction;
		}

		/// <summary>
		/// Leaves transactional state by clearing the stored transaction reference.
		/// Called by SharedKernel's <see cref="WebVella.Erp.SharedKernel.Database.DbConnection.CommitTransaction"/>
		/// or <see cref="WebVella.Erp.SharedKernel.Database.DbConnection.RollbackTransaction"/>.
		/// </summary>
		public void LeaveTransactionalState()
		{
			this.transaction = null;
		}

		#endregion

		#region ===== Static Factory Methods (CreateContext / CloseContext) =====

		/// <summary>
		/// Creates a new ambient CrmDbContext and registers it in the static dictionary.
		/// Also registers the context as <see cref="DbContextAccessor.Current"/> so that
		/// SharedKernel's <see cref="DbRepository"/> utilities can access it.
		/// Mirrors the monolith's <c>DbContext.CreateContext(string)</c> pattern.
		/// </summary>
		/// <param name="connString">PostgreSQL connection string for the CRM database (erp_crm).</param>
		/// <returns>The newly created <see cref="CrmDbContext"/> instance.</returns>
		/// <exception cref="Exception">Thrown if the context cannot be stored or retrieved from the dictionary.</exception>
		public static CrmDbContext CreateContext(string connString)
		{
			connectionString = connString;

			currentDbContextId.Value = Guid.NewGuid().ToString();
			if (!dbContextDict.TryAdd(currentDbContextId.Value, new CrmDbContext()))
				throw new Exception("Cannot create new context and store it into context dictionary");

			Debug.WriteLine($"CRM CreateContext: {currentDbContextId.Value} | dbContextDict count: {dbContextDict.Keys.Count}");

			CrmDbContext? context;
			if (!dbContextDict.TryGetValue(currentDbContextId.Value, out context))
				throw new Exception("Cannot create new context and read it into context dictionary");

			// Register with SharedKernel's DbContextAccessor for DbRepository utility compatibility
			DbContextAccessor.Current = context;

			return context;
		}

		/// <summary>
		/// Closes the current ambient CrmDbContext, removing it from the static dictionary
		/// and clearing <see cref="DbContextAccessor.Current"/>. If a transaction is still
		/// open, it is rolled back and an exception is thrown (preserving monolith behavior).
		/// </summary>
		/// <exception cref="Exception">Thrown if the context has an open transaction.</exception>
		public static void CloseContext()
		{
			if (Current != null)
			{
				if (Current.transaction != null)
				{
					Current.transaction.Rollback();
					throw new Exception("Trying to release database context in transactional state. There is open transaction in created connections.");
				}
			}

			Debug.WriteLine($"CRM CloseContext BEFORE: {currentDbContextId.Value} | dbContextDict count: {dbContextDict.Keys.Count}");
			string? idValue = null;
			if (currentDbContextId != null && !string.IsNullOrWhiteSpace(currentDbContextId.Value))
				idValue = currentDbContextId.Value;

			if (!string.IsNullOrWhiteSpace(idValue))
			{
				CrmDbContext? context;
				dbContextDict.TryRemove(idValue, out context);
				if (context != null)
					context.Dispose();

				currentDbContextId.Value = null;
			}

			// Clear the SharedKernel DbContextAccessor ambient reference.
			// The setter accepts any value including null via the AsyncLocal<IDbContext> backing field.
			DbContextAccessor.Current = null;

			Debug.WriteLine($"CRM CloseContext AFTER: dbContextDict count: {dbContextDict.Keys.Count}");
		}

		#endregion

		#region ===== EF Core Configuration =====

		/// <summary>
		/// Configures the EF Core database provider for migration tooling and design-time scenarios.
		/// When the context is not already configured via DI (e.g., <c>dotnet ef migrations</c>),
		/// falls back to the static <see cref="connectionString"/> or a default localhost connection.
		/// </summary>
		/// <param name="optionsBuilder">The options builder for provider configuration.</param>
		protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
		{
			if (!optionsBuilder.IsConfigured)
			{
				var connStr = connectionString
					?? "Host=localhost;Port=5432;Database=erp_crm;Username=postgres;Password=postgres;"
					 + "Pooling=true;MinPoolSize=1;MaxPoolSize=100;CommandTimeout=120";
				optionsBuilder.UseNpgsql(connStr);
			}
		}

		/// <summary>
		/// Configures the EF Core model for all CRM-owned entities, relationships,
		/// and join tables. Entity configurations match the cumulative state after
		/// all NextPlugin and CrmPlugin patches (20190203, 20190204, 20190206).
		/// </summary>
		/// <param name="modelBuilder">The model builder for entity type configuration.</param>
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

			// ================================================================
			// rec_account — Account entity
			// Entity ID: 2e22b50f-e444-4b62-a171-076e51246939
			// ================================================================
			modelBuilder.Entity<AccountRecord>(e =>
			{
				e.ToTable("rec_account");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Name).HasColumnName("name");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.Type).HasColumnName("type");
				e.Property(x => x.Website).HasColumnName("website");
				e.Property(x => x.Street).HasColumnName("street");
				e.Property(x => x.Street2).HasColumnName("street_2");
				e.Property(x => x.Region).HasColumnName("region");
				e.Property(x => x.PostCode).HasColumnName("post_code");
				e.Property(x => x.FixedPhone).HasColumnName("fixed_phone");
				e.Property(x => x.MobilePhone).HasColumnName("mobile_phone");
				e.Property(x => x.FaxPhone).HasColumnName("fax_phone");
				e.Property(x => x.Notes).HasColumnName("notes");
				e.Property(x => x.LastName).HasColumnName("last_name");
				e.Property(x => x.FirstName).HasColumnName("first_name");
				e.Property(x => x.XSearch).HasColumnName("x_search");
				e.Property(x => x.Email).HasColumnName("email");
				e.Property(x => x.City).HasColumnName("city");
				e.Property(x => x.TaxId).HasColumnName("tax_id");
				// Cross-service references — UUID columns without FK constraints
				// Resolved via Core gRPC calls at the service layer
				e.Property(x => x.CountryId).HasColumnName("country_id");
				e.Property(x => x.LanguageId).HasColumnName("language_id");
				e.Property(x => x.CurrencyId).HasColumnName("currency_id");
				// Intra-CRM salutation reference (salutation_id from Patch20190206)
				e.Property(x => x.SalutationId).HasColumnName("salutation_id");
				// Audit fields
				e.Property(x => x.CreatedOn).HasColumnName("created_on");
				e.Property(x => x.CreatedBy).HasColumnName("created_by");
				e.Property(x => x.LastModifiedOn).HasColumnName("last_modified_on");
				e.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by");
			});

			// ================================================================
			// rec_contact — Contact entity
			// Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
			// ================================================================
			modelBuilder.Entity<ContactRecord>(e =>
			{
				e.ToTable("rec_contact");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Email).HasColumnName("email");
				e.Property(x => x.JobTitle).HasColumnName("job_title");
				e.Property(x => x.FirstName).HasColumnName("first_name");
				e.Property(x => x.LastName).HasColumnName("last_name");
				e.Property(x => x.Notes).HasColumnName("notes");
				e.Property(x => x.FixedPhone).HasColumnName("fixed_phone");
				e.Property(x => x.MobilePhone).HasColumnName("mobile_phone");
				e.Property(x => x.FaxPhone).HasColumnName("fax_phone");
				e.Property(x => x.City).HasColumnName("city");
				e.Property(x => x.Region).HasColumnName("region");
				e.Property(x => x.Street).HasColumnName("street");
				e.Property(x => x.Street2).HasColumnName("street_2");
				e.Property(x => x.PostCode).HasColumnName("post_code");
				e.Property(x => x.XSearch).HasColumnName("x_search");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.Photo).HasColumnName("photo");
				// Cross-service reference — UUID column without FK constraint
				e.Property(x => x.CountryId).HasColumnName("country_id");
				// Audit fields
				e.Property(x => x.CreatedOn).HasColumnName("created_on");
				e.Property(x => x.CreatedBy).HasColumnName("created_by");
				e.Property(x => x.LastModifiedOn).HasColumnName("last_modified_on");
				e.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by");
			});

			// ================================================================
			// rec_case — Case entity
			// Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
			// ================================================================
			modelBuilder.Entity<CaseRecord>(e =>
			{
				e.ToTable("rec_case");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Subject).HasColumnName("subject");
				e.Property(x => x.Description).HasColumnName("description");
				e.Property(x => x.Priority).HasColumnName("priority");
				e.Property(x => x.XSearch).HasColumnName("x_search");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.Number).HasColumnName("number");
				e.Property(x => x.ClosedOn).HasColumnName("closed_on");
				// Intra-CRM FK: case_status_1n_case (OneToMany from case_status → case)
				e.Property(x => x.StatusId).HasColumnName("status_id");
				// Intra-CRM FK: case_type_1n_case (OneToMany from case_type → case)
				e.Property(x => x.TypeId).HasColumnName("type_id");
				// Cross-service/denormalized references — UUID columns without FK constraints
				e.Property(x => x.AccountId).HasColumnName("account_id");
				e.Property(x => x.OwnerId).HasColumnName("owner_id");
				// Audit fields
				e.Property(x => x.CreatedOn).HasColumnName("created_on");
				e.Property(x => x.CreatedBy).HasColumnName("created_by");
				e.Property(x => x.LastModifiedOn).HasColumnName("last_modified_on");
				e.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by");
			});

			// ================================================================
			// rec_address — Address entity
			// Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
			// ================================================================
			modelBuilder.Entity<AddressRecord>(e =>
			{
				e.ToTable("rec_address");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Name).HasColumnName("name");
				e.Property(x => x.Street).HasColumnName("street");
				e.Property(x => x.Street2).HasColumnName("street_2");
				e.Property(x => x.City).HasColumnName("city");
				e.Property(x => x.Region).HasColumnName("region");
				e.Property(x => x.Notes).HasColumnName("notes");
				// Cross-service reference — UUID column without FK constraint
				e.Property(x => x.CountryId).HasColumnName("country_id");
				// Audit fields
				e.Property(x => x.CreatedOn).HasColumnName("created_on");
				e.Property(x => x.CreatedBy).HasColumnName("created_by");
				e.Property(x => x.LastModifiedOn).HasColumnName("last_modified_on");
				e.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by");
			});

			// ================================================================
			// rec_solutation — Salutation entity (typo preserved from source!)
			// Entity ID: f0b64034-e0f6-452e-b82b-88186af6df88
			// Note: entity name "solutation" is a typo from NextPlugin.20190203.cs
			// ================================================================
			modelBuilder.Entity<SalutationRecord>(e =>
			{
				e.ToTable("rec_solutation");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Label).HasColumnName("label");
				e.Property(x => x.IsDefault).HasColumnName("is_default");
				e.Property(x => x.IsEnabled).HasColumnName("is_enabled");
				e.Property(x => x.IsSystem).HasColumnName("is_system");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.SortIndex).HasColumnName("sort_index");
			});

			// ================================================================
			// rec_case_status — Case Status lookup entity (CRM-owned)
			// ================================================================
			modelBuilder.Entity<CaseStatusRecord>(e =>
			{
				e.ToTable("rec_case_status");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Label).HasColumnName("label");
				e.Property(x => x.IsDefault).HasColumnName("is_default");
				e.Property(x => x.IsEnabled).HasColumnName("is_enabled");
				e.Property(x => x.IsSystem).HasColumnName("is_system");
				e.Property(x => x.IsClosed).HasColumnName("is_closed");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.IconClass).HasColumnName("icon_class");
				e.Property(x => x.Color).HasColumnName("color");
				e.Property(x => x.SortIndex).HasColumnName("sort_index");
			});

			// ================================================================
			// rec_case_type — Case Type lookup entity (CRM-owned)
			// ================================================================
			modelBuilder.Entity<CaseTypeRecord>(e =>
			{
				e.ToTable("rec_case_type");
				e.HasKey(x => x.Id);
				e.Property(x => x.Id).HasColumnName("id");
				e.Property(x => x.Label).HasColumnName("label");
				e.Property(x => x.IsDefault).HasColumnName("is_default");
				e.Property(x => x.IsEnabled).HasColumnName("is_enabled");
				e.Property(x => x.IsSystem).HasColumnName("is_system");
				e.Property(x => x.LScope).HasColumnName("l_scope");
				e.Property(x => x.IconClass).HasColumnName("icon_class");
				e.Property(x => x.Color).HasColumnName("color");
				e.Property(x => x.SortIndex).HasColumnName("sort_index");
			});

			// ================================================================
			// Many-to-Many Join Tables (shadow entities)
			// ================================================================

			// rel_account_nn_contact — Account ↔ Contact many-to-many
			modelBuilder.Entity("rel_account_nn_contact", e =>
			{
				e.ToTable("rel_account_nn_contact");
				e.Property<Guid>("origin_id");
				e.Property<Guid>("target_id");
				e.HasKey("origin_id", "target_id");
			});

			// rel_account_nn_case — Account ↔ Case many-to-many
			modelBuilder.Entity("rel_account_nn_case", e =>
			{
				e.ToTable("rel_account_nn_case");
				e.Property<Guid>("origin_id");
				e.Property<Guid>("target_id");
				e.HasKey("origin_id", "target_id");
			});

			// rel_address_nn_account — Address ↔ Account many-to-many
			modelBuilder.Entity("rel_address_nn_account", e =>
			{
				e.ToTable("rel_address_nn_account");
				e.Property<Guid>("origin_id");
				e.Property<Guid>("target_id");
				e.HasKey("origin_id", "target_id");
			});

			// ================================================================
			// One-to-Many FK Relations (within CRM boundary)
			// These configure FK constraints within the CRM database.
			// ================================================================

			// case_status_1n_case: CaseStatus → Case (via status_id)
			modelBuilder.Entity<CaseRecord>()
				.HasOne<CaseStatusRecord>()
				.WithMany()
				.HasForeignKey(c => c.StatusId)
				.HasConstraintName("fk_case_status_1n_case")
				.IsRequired(false)
				.OnDelete(DeleteBehavior.SetNull);

			// case_type_1n_case: CaseType → Case (via type_id)
			modelBuilder.Entity<CaseRecord>()
				.HasOne<CaseTypeRecord>()
				.WithMany()
				.HasForeignKey(c => c.TypeId)
				.HasConstraintName("fk_case_type_1n_case")
				.IsRequired(false)
				.OnDelete(DeleteBehavior.SetNull);

			// solutation_1n_contact is preserved as a conceptual relation.
			// Note: In the final monolith state (after Patch20190206), the original
			// solutation_1n_contact relation was deleted and solutation_id was removed
			// from contact. The CRM microservice preserves the salutation→account
			// relation via account.salutation_id mapped above.

			// ================================================================
			// Cross-service references (denormalized UUID fields, NO FK constraints)
			// These columns store foreign entity IDs from other microservices:
			//   - currency_1n_account: account.currency_id → Core currency
			//   - country_1n_address: address.country_id → Core country
			//   - country_1n_account: account.country_id → Core country
			//   - language_1n_account: account.language_id → Core language
			//   - country_1n_contact: contact.country_id → Core country
			// Resolution: Via Core service gRPC calls at the service layer.
			// No EF Core FK configuration needed — these are plain Guid? columns.
			// ================================================================
		}

		#endregion

		#region ===== Dispose =====

		/// <summary>
		/// Disposes the CrmDbContext, performing cleanup of the ambient context
		/// if active, and then delegating to EF Core's base disposal.
		/// EF Core's DbContext implements IDisposable directly with a virtual Dispose()
		/// method (not Dispose(bool)), so we override Dispose() instead.
		/// </summary>
		public override void Dispose()
		{
			// If this instance is the current ambient context, clean up
			if (Current == this)
			{
				string? idValue = null;
				if (currentDbContextId != null && !string.IsNullOrWhiteSpace(currentDbContextId.Value))
				{
					idValue = currentDbContextId.Value;
				}

				if (!string.IsNullOrWhiteSpace(idValue))
				{
					dbContextDict.TryRemove(idValue, out _);
					currentDbContextId.Value = null;
				}

				// Clear the SharedKernel DbContextAccessor ambient reference
				DbContextAccessor.Current = null;
			}

			base.Dispose();
		}

		#endregion
	}
}
