// =============================================================================
// CrmDbContextModelSnapshot.cs — EF Core Model Snapshot for CRM Microservice
// =============================================================================
// Represents the CURRENT accumulated state of the CRM database model as known
// by EF Core's migration framework. When a developer runs
// `dotnet ef migrations add <Name>`, EF Core compares the model built by
// CrmDbContext.OnModelCreating against this snapshot to compute the diff and
// generate a new migration.
//
// After the initial migration (20250101000000_InitialCrmSchema), this snapshot
// exactly matches the BuildTargetModel in the designer file, because the
// snapshot always reflects the final state after all applied migrations.
//
// Entity tables (7):
//   rec_account, rec_contact, rec_case, rec_case_status, rec_case_type,
//   rec_address, rec_salutation
//
// Join tables (3):
//   rel_account_nn_contact, rel_account_nn_case, rel_address_nn_account
//
// Key design decisions:
//   - Cross-service UUID columns (country_id, language_id, currency_id,
//     created_by, last_modified_by) have NO navigation properties or FK
//     constraints — they are resolved via Core gRPC calls at the service layer.
//   - Intra-service FK columns (status_id, type_id on case) have indexes
//     defined here; actual FK constraints are managed in migration SQL.
//   - The rec_case.number column uses PostgreSQL identity (serial) via
//     NpgsqlValueGenerationStrategy.IdentityByDefaultColumn.
//   - No seed data in the snapshot — seed data is only in the migration Up().
//
// Source references:
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (entity creation)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (fields + relations)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (schema updates)
//   - WebVella.Erp.Plugins.Crm/CrmPlugin._.cs (CRM patch framework)
//   - WebVella.Erp/Database/DBTypeConverter.cs (ERP ↔ PostgreSQL type mapping)
// =============================================================================

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using WebVella.Erp.Service.Crm.Database;

namespace WebVella.Erp.Service.Crm.Database.Migrations
{
    /// <summary>
    /// EF Core model snapshot for the CRM microservice's <see cref="CrmDbContext"/>.
    /// This file is auto-maintained by EF Core migration tooling and represents
    /// the current accumulated state of all CRM database migrations.
    /// </summary>
    [DbContext(typeof(CrmDbContext))]
    partial class CrmDbContextModelSnapshot : ModelSnapshot
    {
        /// <summary>
        /// Builds the complete current model state for the CRM database.
        /// Defines all 7 entity tables and 3 join tables with their columns,
        /// types, defaults, constraints, and indexes.
        /// </summary>
        /// <param name="modelBuilder">The model builder provided by EF Core migration infrastructure.</param>
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.1")
                .HasAnnotation("Relational:MaxIdentifierLength", 63)
                .HasAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            // ================================================================
            // rec_account — Account entity
            // Entity ID: 2e22b50f-e444-4b62-a171-076e51246939
            // Source: NextPlugin.20190203 + 20190204 + 20190206 cumulative
            // ================================================================
            modelBuilder.Entity("rec_account", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<string>("name")
                    .IsRequired()
                    .HasColumnType("text");

                b.Property<string>("l_scope")
                    .IsRequired()
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<string>("type")
                    .HasColumnType("text")
                    .HasDefaultValue("company");

                b.Property<string>("website")
                    .HasColumnType("text");

                b.Property<string>("street")
                    .HasColumnType("text");

                b.Property<string>("street_2")
                    .HasColumnType("text");

                b.Property<string>("city")
                    .HasColumnType("text");

                b.Property<string>("region")
                    .HasColumnType("text");

                b.Property<string>("post_code")
                    .HasColumnType("text");

                // Cross-service reference: country resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("country_id")
                    .HasColumnType("uuid");

                b.Property<string>("fixed_phone")
                    .HasColumnType("text");

                b.Property<string>("mobile_phone")
                    .HasColumnType("text");

                b.Property<string>("fax_phone")
                    .HasColumnType("text");

                b.Property<string>("email")
                    .HasColumnType("text");

                b.Property<string>("notes")
                    .HasColumnType("text");

                b.Property<string>("last_name")
                    .HasColumnType("text");

                b.Property<string>("first_name")
                    .HasColumnType("text");

                b.Property<string>("x_search")
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<string>("tax_id")
                    .HasColumnType("text");

                // Cross-service reference: language resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("language_id")
                    .HasColumnType("uuid");

                // Cross-service reference: currency resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("currency_id")
                    .HasColumnType("uuid");

                // Intra-CRM salutation reference (from Patch20190206)
                b.Property<Guid?>("salutation_id")
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("'87c08ee1-8d4d-4c89-9b37-4e3cc3f98698'");

                // Audit fields
                b.Property<DateTime?>("created_on")
                    .HasColumnType("timestamptz")
                    .HasDefaultValueSql("now()");

                // Cross-service reference: user resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("created_by")
                    .HasColumnType("uuid");

                b.Property<DateTime?>("last_modified_on")
                    .HasColumnType("timestamptz");

                // Cross-service reference: user resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("last_modified_by")
                    .HasColumnType("uuid");

                b.HasKey("id");

                b.ToTable("rec_account");
            });

            // ================================================================
            // rec_contact — Contact entity
            // Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
            // Source: NextPlugin.20190204 + 20190206 cumulative
            // ================================================================
            modelBuilder.Entity("rec_contact", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<string>("first_name")
                    .HasColumnType("text");

                b.Property<string>("last_name")
                    .HasColumnType("text");

                b.Property<string>("email")
                    .HasColumnType("text");

                b.Property<string>("job_title")
                    .HasColumnType("text");

                b.Property<string>("notes")
                    .HasColumnType("text");

                b.Property<string>("fixed_phone")
                    .HasColumnType("text");

                b.Property<string>("mobile_phone")
                    .HasColumnType("text");

                b.Property<string>("fax_phone")
                    .HasColumnType("text");

                b.Property<string>("city")
                    .HasColumnType("text");

                b.Property<string>("region")
                    .HasColumnType("text");

                b.Property<string>("street")
                    .HasColumnType("text");

                b.Property<string>("street_2")
                    .HasColumnType("text");

                b.Property<string>("post_code")
                    .HasColumnType("text");

                // Cross-service reference: country resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("country_id")
                    .HasColumnType("uuid");

                // Intra-CRM salutation reference (from Patch20190206)
                b.Property<Guid?>("salutation_id")
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("'87c08ee1-8d4d-4c89-9b37-4e3cc3f98698'");

                // InputImageField stores path/URL as text
                b.Property<string>("photo")
                    .HasColumnType("text");

                b.Property<string>("x_search")
                    .HasColumnType("text")
                    .HasDefaultValue("");

                // Audit fields
                b.Property<DateTime?>("created_on")
                    .HasColumnType("timestamptz")
                    .HasDefaultValueSql("now()");

                b.Property<Guid?>("created_by")
                    .HasColumnType("uuid");

                b.Property<DateTime?>("last_modified_on")
                    .HasColumnType("timestamptz");

                b.Property<Guid?>("last_modified_by")
                    .HasColumnType("uuid");

                b.HasKey("id");

                b.ToTable("rec_contact");
            });

            // ================================================================
            // rec_case — Case entity
            // Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
            // Source: NextPlugin.20190203 + 20190206 cumulative
            // ================================================================
            modelBuilder.Entity("rec_case", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                // Cross-service/denormalized: account UUID without FK
                b.Property<Guid?>("account_id")
                    .HasColumnType("uuid");

                // Audit fields
                b.Property<DateTime?>("created_on")
                    .HasColumnType("timestamptz")
                    .HasDefaultValueSql("now()");

                // Cross-service reference: user resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("created_by")
                    .HasColumnType("uuid");

                // Cross-service reference: owner user resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("owner_id")
                    .HasColumnType("uuid");

                b.Property<string>("description")
                    .HasColumnType("text");

                b.Property<string>("subject")
                    .HasColumnType("text");

                // AutoNumber field: PostgreSQL identity (serial) column
                b.Property<int>("number")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("integer");

                NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("number"));

                b.Property<DateTime?>("closed_on")
                    .HasColumnType("timestamptz");

                b.Property<string>("l_scope")
                    .IsRequired()
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<string>("priority")
                    .HasColumnType("text")
                    .HasDefaultValue("medium");

                // Intra-service FK to rec_case_status (case_status_1n_case)
                b.Property<Guid?>("status_id")
                    .HasColumnType("uuid");

                // Intra-service FK to rec_case_type (case_type_1n_case)
                b.Property<Guid?>("type_id")
                    .HasColumnType("uuid");

                b.Property<string>("x_search")
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<DateTime?>("last_modified_on")
                    .HasColumnType("timestamptz");

                // Cross-service reference: user resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("last_modified_by")
                    .HasColumnType("uuid");

                b.HasKey("id");

                b.HasIndex("status_id");

                b.HasIndex("type_id");

                b.ToTable("rec_case");
            });

            // ================================================================
            // rec_case_status — Case Status lookup entity (CRM-owned)
            // Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27
            // Source: NextPlugin.20190203
            // ================================================================
            modelBuilder.Entity("rec_case_status", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<bool>("is_default")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<string>("label")
                    .IsRequired()
                    .HasColumnType("text");

                b.Property<decimal>("sort_index")
                    .HasColumnType("numeric")
                    .HasDefaultValue(0m);

                b.Property<bool>("is_closed")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<bool>("is_system")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<bool>("is_enabled")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(true);

                b.Property<string>("l_scope")
                    .IsRequired()
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<string>("icon_class")
                    .HasColumnType("text");

                b.Property<string>("color")
                    .HasColumnType("text");

                b.HasKey("id");

                b.ToTable("rec_case_status");
            });

            // ================================================================
            // rec_case_type — Case Type lookup entity (CRM-owned)
            // Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad
            // Source: NextPlugin.20190203
            // ================================================================
            modelBuilder.Entity("rec_case_type", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<bool>("is_default")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<bool>("is_enabled")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(true);

                b.Property<bool>("is_system")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<string>("l_scope")
                    .IsRequired()
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.Property<decimal>("sort_index")
                    .HasColumnType("numeric")
                    .HasDefaultValue(0m);

                b.Property<string>("label")
                    .IsRequired()
                    .HasColumnType("text");

                b.Property<string>("icon_class")
                    .HasColumnType("text");

                b.Property<string>("color")
                    .HasColumnType("text");

                b.HasKey("id");

                b.ToTable("rec_case_type");
            });

            // ================================================================
            // rec_address — Address entity
            // Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
            // Source: NextPlugin.20190204
            // ================================================================
            modelBuilder.Entity("rec_address", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<string>("name")
                    .HasColumnType("text");

                b.Property<string>("street")
                    .HasColumnType("text");

                b.Property<string>("street_2")
                    .HasColumnType("text");

                b.Property<string>("city")
                    .HasColumnType("text");

                b.Property<string>("region")
                    .HasColumnType("text");

                // Cross-service reference: country resolved via Core gRPC — no FK constraint
                b.Property<Guid?>("country_id")
                    .HasColumnType("uuid");

                b.Property<string>("notes")
                    .HasColumnType("text");

                // Audit fields
                b.Property<DateTime?>("created_on")
                    .HasColumnType("timestamptz")
                    .HasDefaultValueSql("now()");

                b.Property<Guid?>("created_by")
                    .HasColumnType("uuid");

                b.Property<DateTime?>("last_modified_on")
                    .HasColumnType("timestamptz");

                b.Property<Guid?>("last_modified_by")
                    .HasColumnType("uuid");

                b.HasKey("id");

                b.ToTable("rec_address");
            });

            // ================================================================
            // rec_salutation — Salutation entity
            // Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def
            // Source: NextPlugin.20190206 (corrected from original "solutation" typo)
            // ================================================================
            modelBuilder.Entity("rec_salutation", b =>
            {
                b.Property<Guid>("id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid")
                    .HasDefaultValueSql("uuid_generate_v1()");

                b.Property<bool>("is_default")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<bool>("is_enabled")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(true);

                b.Property<bool>("is_system")
                    .IsRequired()
                    .HasColumnType("boolean")
                    .HasDefaultValue(false);

                b.Property<string>("label")
                    .IsRequired()
                    .HasColumnType("text");

                b.Property<decimal>("sort_index")
                    .HasColumnType("numeric")
                    .HasDefaultValue(0m);

                b.Property<string>("l_scope")
                    .IsRequired()
                    .HasColumnType("text")
                    .HasDefaultValue("");

                b.HasKey("id");

                b.HasIndex("label")
                    .IsUnique();

                b.ToTable("rec_salutation");
            });

            // ================================================================
            // Many-to-Many Join Tables (shadow entities)
            // Source: NextPlugin.20190204 relation definitions
            // ================================================================

            // rel_account_nn_contact — Account ↔ Contact many-to-many
            modelBuilder.Entity("rel_account_nn_contact", b =>
            {
                b.Property<Guid>("origin_id")
                    .HasColumnType("uuid");

                b.Property<Guid>("target_id")
                    .HasColumnType("uuid");

                b.HasKey("origin_id", "target_id");

                b.HasIndex("origin_id");

                b.HasIndex("target_id");

                b.ToTable("rel_account_nn_contact");
            });

            // rel_account_nn_case — Account ↔ Case many-to-many
            modelBuilder.Entity("rel_account_nn_case", b =>
            {
                b.Property<Guid>("origin_id")
                    .HasColumnType("uuid");

                b.Property<Guid>("target_id")
                    .HasColumnType("uuid");

                b.HasKey("origin_id", "target_id");

                b.HasIndex("origin_id");

                b.HasIndex("target_id");

                b.ToTable("rel_account_nn_case");
            });

            // rel_address_nn_account — Address ↔ Account many-to-many
            modelBuilder.Entity("rel_address_nn_account", b =>
            {
                b.Property<Guid>("origin_id")
                    .HasColumnType("uuid");

                b.Property<Guid>("target_id")
                    .HasColumnType("uuid");

                b.HasKey("origin_id", "target_id");

                b.HasIndex("origin_id");

                b.HasIndex("target_id");

                b.ToTable("rel_address_nn_account");
            });

#pragma warning restore 612, 618
        }
    }
}
