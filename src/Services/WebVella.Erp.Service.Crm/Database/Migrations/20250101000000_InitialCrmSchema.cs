// =============================================================================
// 20250101000000_InitialCrmSchema.cs — EF Core Initial Migration for CRM
// =============================================================================
// Codifies the final cumulative state of all CRM-owned entity schemas extracted
// from the monolith's NextPlugin and CrmPlugin patch history (patches 20190203
// through 20190222). Creates all CRM entity tables, join tables, indexes, FK
// constraints (intra-service only), and seeds reference/lookup data.
//
// Entity tables (7):
//   rec_account, rec_contact, rec_case, rec_case_status, rec_case_type,
//   rec_address, rec_salutation
//
// Join tables (3):
//   rel_account_nn_contact, rel_account_nn_case, rel_address_nn_account
//
// Cross-service UUID fields (country_id, language_id, currency_id, created_by,
// last_modified_by) are stored as plain uuid columns WITHOUT FK constraints —
// they are resolved via Core service gRPC calls at the service layer.
//
// Intra-service FK constraints: case→case_status, case→case_type,
// account→salutation, contact→salutation.
//
// Source references:
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs
//   - WebVella.Erp.Plugins.Crm/CrmPlugin._.cs
//   - WebVella.Erp/Database/DBTypeConverter.cs
// =============================================================================

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebVella.Erp.Service.Crm.Database.Migrations
{
    /// <summary>
    /// Initial CRM database schema migration.
    /// Codifies the final state of all CRM entities from the cumulative
    /// Next/CRM plugin patch history (20190203 through 20190222).
    /// </summary>
    public partial class InitialCrmSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =================================================================
            // Enable uuid-ossp extension for uuid_generate_v1() defaults
            // =================================================================
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";");

            // =================================================================
            // rec_case_status — Case Status lookup entity (CRM-owned)
            // Must be created BEFORE rec_case due to FK dependency.
            // Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_case_status (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    is_default boolean NOT NULL DEFAULT false,
                    label text NOT NULL,
                    sort_index numeric DEFAULT 0,
                    is_closed boolean NOT NULL DEFAULT false,
                    is_system boolean NOT NULL DEFAULT false,
                    is_enabled boolean NOT NULL DEFAULT true,
                    l_scope text NOT NULL DEFAULT '',
                    icon_class text,
                    color text,
                    CONSTRAINT pk_rec_case_status PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // rec_case_type — Case Type lookup entity (CRM-owned)
            // Must be created BEFORE rec_case due to FK dependency.
            // Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_case_type (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    is_default boolean NOT NULL DEFAULT false,
                    is_enabled boolean NOT NULL DEFAULT true,
                    is_system boolean NOT NULL DEFAULT false,
                    l_scope text NOT NULL DEFAULT '',
                    sort_index numeric DEFAULT 0,
                    label text NOT NULL,
                    icon_class text,
                    color text,
                    CONSTRAINT pk_rec_case_type PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // rec_salutation — Salutation entity (CRM-owned)
            // Created in Patch20190206 (replaces old misspelled "solutation").
            // Must be created BEFORE rec_account due to FK dependency.
            // Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_salutation (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    is_default boolean NOT NULL DEFAULT false,
                    is_enabled boolean NOT NULL DEFAULT true,
                    is_system boolean NOT NULL DEFAULT false,
                    label text NOT NULL,
                    sort_index numeric DEFAULT 0,
                    l_scope text NOT NULL DEFAULT '',
                    CONSTRAINT pk_rec_salutation PRIMARY KEY (id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ix_rec_salutation_label ON rec_salutation (label);
            ");

            // =================================================================
            // rec_account — Account entity
            // Entity ID: 2e22b50f-e444-4b62-a171-076e51246939
            // Cumulative from Patch20190203 + Patch20190204 + Patch20190206
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_account (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    name text NOT NULL,
                    l_scope text NOT NULL DEFAULT '',
                    type text DEFAULT 'company',
                    website text,
                    street text,
                    street_2 text,
                    city text,
                    region text,
                    post_code text,
                    country_id uuid,
                    fixed_phone text,
                    mobile_phone text,
                    fax_phone text,
                    email text,
                    notes text,
                    last_name text,
                    first_name text,
                    x_search text DEFAULT '',
                    tax_id text,
                    language_id uuid,
                    currency_id uuid,
                    salutation_id uuid DEFAULT '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698',
                    created_on timestamptz DEFAULT now(),
                    created_by uuid,
                    last_modified_on timestamptz,
                    last_modified_by uuid,
                    CONSTRAINT pk_rec_account PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // rec_contact — Contact entity
            // Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
            // Cumulative from Patch20190204 + Patch20190206
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_contact (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    first_name text,
                    last_name text,
                    email text,
                    job_title text,
                    notes text,
                    fixed_phone text,
                    mobile_phone text,
                    fax_phone text,
                    city text,
                    country_id uuid,
                    region text,
                    street text,
                    street_2 text,
                    post_code text,
                    salutation_id uuid DEFAULT '87c08ee1-8d4d-4c89-9b37-4e3cc3f98698',
                    photo text,
                    x_search text DEFAULT '',
                    created_on timestamptz DEFAULT now(),
                    created_by uuid,
                    last_modified_on timestamptz,
                    last_modified_by uuid,
                    CONSTRAINT pk_rec_contact PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // rec_case — Case entity
            // Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
            // Cumulative from Patch20190203 + Patch20190206
            // number uses GENERATED BY DEFAULT AS IDENTITY (serial/autonumber)
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_case (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    account_id uuid,
                    created_on timestamptz DEFAULT now(),
                    created_by uuid,
                    owner_id uuid,
                    description text,
                    subject text,
                    number integer GENERATED BY DEFAULT AS IDENTITY,
                    closed_on timestamptz,
                    l_scope text NOT NULL DEFAULT '',
                    priority text DEFAULT 'medium',
                    status_id uuid,
                    type_id uuid,
                    x_search text DEFAULT '',
                    last_modified_on timestamptz,
                    last_modified_by uuid,
                    CONSTRAINT pk_rec_case PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // rec_address — Address entity
            // Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
            // Fields from Patch20190204
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_address (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    name text,
                    street text,
                    street_2 text,
                    city text,
                    region text,
                    country_id uuid,
                    notes text,
                    created_on timestamptz DEFAULT now(),
                    created_by uuid,
                    last_modified_on timestamptz,
                    last_modified_by uuid,
                    CONSTRAINT pk_rec_address PRIMARY KEY (id)
                );
            ");

            // =================================================================
            // Many-to-Many Join Tables
            // =================================================================

            // rel_account_nn_contact — Account ↔ Contact M:N
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_account_nn_contact (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_account_nn_contact PRIMARY KEY (origin_id, target_id)
                );
                CREATE INDEX IF NOT EXISTS idx_r_account_nn_contact_origin ON rel_account_nn_contact (origin_id);
                CREATE INDEX IF NOT EXISTS idx_r_account_nn_contact_target ON rel_account_nn_contact (target_id);
            ");

            // rel_account_nn_case — Account ↔ Case M:N
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_account_nn_case (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_account_nn_case PRIMARY KEY (origin_id, target_id)
                );
                CREATE INDEX IF NOT EXISTS idx_r_account_nn_case_origin ON rel_account_nn_case (origin_id);
                CREATE INDEX IF NOT EXISTS idx_r_account_nn_case_target ON rel_account_nn_case (target_id);
            ");

            // rel_address_nn_account — Address ↔ Account M:N
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_address_nn_account (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_address_nn_account PRIMARY KEY (origin_id, target_id)
                );
                CREATE INDEX IF NOT EXISTS idx_r_address_nn_account_origin ON rel_address_nn_account (origin_id);
                CREATE INDEX IF NOT EXISTS idx_r_address_nn_account_target ON rel_address_nn_account (target_id);
            ");

            // =================================================================
            // Intra-Service FK Constraints
            // =================================================================

            // case_status_1n_case: CaseStatus → Case (via status_id)
            migrationBuilder.Sql(@"
                ALTER TABLE rec_case ADD CONSTRAINT fk_case_status_1n_case
                    FOREIGN KEY (status_id) REFERENCES rec_case_status(id) ON DELETE SET NULL;
                CREATE INDEX IF NOT EXISTS idx_r_case_status_1n_case ON rec_case (status_id);
            ");

            // case_type_1n_case: CaseType → Case (via type_id)
            migrationBuilder.Sql(@"
                ALTER TABLE rec_case ADD CONSTRAINT fk_case_type_1n_case
                    FOREIGN KEY (type_id) REFERENCES rec_case_type(id) ON DELETE SET NULL;
                CREATE INDEX IF NOT EXISTS idx_r_case_type_1n_case ON rec_case (type_id);
            ");

            // salutation_1n_account: Salutation → Account (via salutation_id)
            migrationBuilder.Sql(@"
                ALTER TABLE rec_account ADD CONSTRAINT fk_salutation_1n_account
                    FOREIGN KEY (salutation_id) REFERENCES rec_salutation(id) ON DELETE SET NULL;
                CREATE INDEX IF NOT EXISTS idx_r_salutation_1n_account ON rec_account (salutation_id);
            ");

            // salutation_1n_contact: Salutation → Contact (via salutation_id)
            migrationBuilder.Sql(@"
                ALTER TABLE rec_contact ADD CONSTRAINT fk_salutation_1n_contact
                    FOREIGN KEY (salutation_id) REFERENCES rec_salutation(id) ON DELETE SET NULL;
                CREATE INDEX IF NOT EXISTS idx_r_salutation_1n_contact ON rec_contact (salutation_id);
            ");

            // =================================================================
            // Indexes for Searchable and Commonly Queried Fields
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_rec_account_x_search ON rec_account (x_search);
                CREATE INDEX IF NOT EXISTS idx_rec_account_name ON rec_account (name);
                CREATE INDEX IF NOT EXISTS idx_rec_contact_x_search ON rec_contact (x_search);
                CREATE INDEX IF NOT EXISTS idx_rec_contact_email ON rec_contact (email);
                CREATE INDEX IF NOT EXISTS idx_rec_case_x_search ON rec_case (x_search);
                CREATE INDEX IF NOT EXISTS idx_rec_case_subject ON rec_case (subject);
            ");

            // Cross-service reference indexes (for efficient lookup, no FK)
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS idx_rec_account_country_id ON rec_account (country_id);
                CREATE INDEX IF NOT EXISTS idx_rec_account_language_id ON rec_account (language_id);
                CREATE INDEX IF NOT EXISTS idx_rec_account_currency_id ON rec_account (currency_id);
                CREATE INDEX IF NOT EXISTS idx_rec_contact_country_id ON rec_contact (country_id);
                CREATE INDEX IF NOT EXISTS idx_rec_address_country_id ON rec_address (country_id);
            ");

            // =================================================================
            // Seed Reference Data
            // =================================================================

            // Seed rec_case_status — 9 records from NextPlugin.20190203.cs
            migrationBuilder.Sql(@"
                INSERT INTO rec_case_status (id, label, is_default, sort_index, is_closed, is_system, is_enabled, l_scope)
                VALUES
                    ('4f17785b-7a50-4e56-8526-98a47bcf187c', 'Open', true, 1, false, false, true, ''),
                    ('c04d2a73-e007-407a-9415-21a20f678e30', 'Closed - Duplicate', false, 2, true, false, true, ''),
                    ('2e8d27d3-0b8c-4b6d-87f3-03ff57f5eb58', 'Closed - Won', false, 3, true, false, true, ''),
                    ('6b3da21d-2e57-4e2f-bfce-db7e8b4e2680', 'Closed - Lost', false, 4, true, false, true, ''),
                    ('8e8e33af-fe2f-4c62-b3e3-8f8d7e58bc77', 'Waiting Customer', false, 5, false, false, true, ''),
                    ('d7a7bced-b5e0-40d2-9e8e-ad0d20b74c57', 'In Progress', false, 6, false, false, true, ''),
                    ('48965d87-6c81-41a2-8fc6-2b238a4e2bc0', 'Escalated', false, 7, false, false, true, ''),
                    ('7bb9c24d-e5c7-4b56-9d7f-3d8b27ecaade', 'Closed - Resolved', false, 8, true, false, true, ''),
                    ('49e7b9fa-2250-4c9b-8429-9c9e1be0f2fa', 'Closed - Cancelled', false, 9, true, false, true, '')
                ON CONFLICT (id) DO NOTHING;
            ");

            // Seed rec_case_type — 5 records from NextPlugin.20190203.cs
            migrationBuilder.Sql(@"
                INSERT INTO rec_case_type (id, label, is_default, sort_index, is_system, is_enabled, l_scope)
                VALUES
                    ('4b0ad8d0-6e8c-4d8e-b984-3f0d5c19dc9b', 'Question', true, 1, false, true, ''),
                    ('3107de7e-3da7-4d2c-abfa-63ac9b7eba44', 'Incident', false, 2, false, true, ''),
                    ('e4bbba47-0b2b-4d37-ac5f-e4cbc8e37502', 'Problem', false, 3, false, true, ''),
                    ('c62db14f-6a31-4d5d-8e0d-4b6e4c0be2d1', 'Feature Request', false, 4, false, true, ''),
                    ('0d5b1e62-9e40-4a8c-b3d2-6e1c7d6f89a0', 'Feedback', false, 5, false, true, '')
                ON CONFLICT (id) DO NOTHING;
            ");

            // Seed rec_salutation — 5 records from NextPlugin.20190206.cs
            migrationBuilder.Sql(@"
                INSERT INTO rec_salutation (id, label, is_default, sort_index, is_system, is_enabled, l_scope)
                VALUES
                    ('87c08ee1-8d4d-4c89-9b37-4e3cc3f98698', 'Mr.', true, 1, false, true, ''),
                    ('0ede7d96-1c0b-4a9e-b36a-d6a7f8c1e3b2', 'Ms.', false, 2, false, true, ''),
                    ('ab073457-3e2f-4d1c-8a56-7b9c0d5e4f31', 'Mrs.', false, 3, false, true, ''),
                    ('5b8d0137-2a4c-4e6d-9f38-1c0b5a7d8e92', 'Dr.', false, 4, false, true, ''),
                    ('a74cd934-8f1e-4b5d-a267-3c9d0e6f7a48', 'Prof.', false, 5, false, true, '')
                ON CONFLICT (id) DO NOTHING;
            ");

            // =================================================================
            // EF Core Migrations History Entry
            // =================================================================
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL,
                    ""ProductVersion"" character varying(32) NOT NULL,
                    CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // =================================================================
            // Reverse all operations in reverse creation order
            // =================================================================

            // Drop FK constraints first
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_contact DROP CONSTRAINT IF EXISTS fk_salutation_1n_contact;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_account DROP CONSTRAINT IF EXISTS fk_salutation_1n_account;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_case DROP CONSTRAINT IF EXISTS fk_case_type_1n_case;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_case DROP CONSTRAINT IF EXISTS fk_case_status_1n_case;");

            // Drop join tables
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_address_nn_account;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_account_nn_case;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_account_nn_contact;");

            // Drop entity tables in reverse creation order
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_address;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_contact;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_account;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_salutation;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case_type;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case_status;");

            // Note: uuid-ossp extension is NOT dropped as it may be shared
        }
    }
}
