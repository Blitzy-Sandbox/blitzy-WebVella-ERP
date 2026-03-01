using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Crm.Database.Migrations
{
    /// <summary>
    /// Initial CRM database schema migration.
    /// Codifies the final state of all CRM entities from the cumulative
    /// Next/CRM plugin patch history (20190203 through 20190222).
    ///
    /// Entity tables created:
    ///   - rec_account      (Entity ID: 2e22b50f-e444-4b62-a171-076e51246939)
    ///   - rec_contact      (Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0)
    ///   - rec_case         (Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c)
    ///   - rec_case_status  (Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27)
    ///   - rec_case_type    (Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad)
    ///   - rec_address      (Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0)
    ///   - rec_salutation   (Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def)
    ///
    /// Join tables created:
    ///   - rel_account_nn_contact
    ///   - rel_account_nn_case
    ///   - rel_address_nn_account
    ///
    /// Intra-service FK constraints:
    ///   - fk_case_status_1n_case      (rec_case.status_id -> rec_case_status.id)
    ///   - fk_case_type_1n_case        (rec_case.type_id -> rec_case_type.id)
    ///   - fk_salutation_1n_account    (rec_account.salutation_id -> rec_salutation.id)
    ///   - fk_salutation_1n_contact    (rec_contact.salutation_id -> rec_salutation.id)
    ///
    /// Cross-service UUID references (NO FK constraints):
    ///   - country_id, language_id, currency_id -> Core service
    ///   - created_by, last_modified_by, owner_id -> Core service (user entity)
    ///
    /// Seed data:
    ///   - 9 case_status records (from NextPlugin.20190203.cs)
    ///   - 5 case_type records   (from NextPlugin.20190203.cs)
    ///   - 5 salutation records  (from NextPlugin.20190206.cs)
    /// </summary>
    [Migration("20250101000000_InitialCrmSchema")]
    public partial class InitialCrmSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // STEP 1: Enable uuid-ossp extension for uuid_generate_v1()
            // ============================================================
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";");

            // ============================================================
            // STEP 2: Create lookup/reference tables first (referenced by FKs)
            // ============================================================

            // -------------------------------------------------------
            // 2.1 rec_case_status — CRM-owned lookup table
            //     Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27
            //     Source: NextPlugin.20190203.cs (lines 1079-1355)
            // -------------------------------------------------------
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

            // -------------------------------------------------------
            // 2.2 rec_case_type — CRM-owned lookup table
            //     Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad
            //     Source: NextPlugin.20190203.cs (lines 3410-3652)
            // -------------------------------------------------------
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

            // -------------------------------------------------------
            // 2.3 rec_salutation — CRM-owned lookup table
            //     Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def
            //     Source: NextPlugin.20190206.cs (lines 613-828)
            //     Replaces the old misspelled "solutation" entity
            //     which was deleted in Patch20190206.
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_salutation (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    is_default boolean NOT NULL DEFAULT false,
                    is_enabled boolean NOT NULL DEFAULT true,
                    is_system boolean NOT NULL DEFAULT false,
                    label text NOT NULL UNIQUE,
                    sort_index numeric DEFAULT 0,
                    l_scope text NOT NULL DEFAULT '',
                    CONSTRAINT pk_rec_salutation PRIMARY KEY (id)
                );
            ");

            // ============================================================
            // STEP 3: Create main entity tables
            // ============================================================

            // -------------------------------------------------------
            // 3.1 rec_account — Account entity
            //     Entity ID: 2e22b50f-e444-4b62-a171-076e51246939
            //     Source: NextPlugin.20190203.cs + 20190204.cs + 20190206.cs
            //     Cross-service refs (NO FK): country_id, language_id,
            //       currency_id, created_by, last_modified_by
            //     Intra-service FK: salutation_id -> rec_salutation
            // -------------------------------------------------------
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

            // -------------------------------------------------------
            // 3.2 rec_contact — Contact entity
            //     Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0
            //     Source: NextPlugin.20190204.cs + 20190206.cs
            //     Cross-service refs (NO FK): country_id, created_by,
            //       last_modified_by
            //     Intra-service FK: salutation_id -> rec_salutation
            // -------------------------------------------------------
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

            // -------------------------------------------------------
            // 3.3 rec_case — Case entity
            //     Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c
            //     Source: NextPlugin.20190203.cs + 20190206.cs
            //     Cross-service refs (NO FK): created_by, owner_id,
            //       last_modified_by
            //     Intra-service FKs: status_id -> rec_case_status,
            //       type_id -> rec_case_type
            //     account_id: intra-service ref to rec_account
            //     number: PostgreSQL SERIAL (autonumber in monolith)
            //     priority default: 'low' (from source line 1709)
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rec_case (
                    id uuid NOT NULL DEFAULT uuid_generate_v1(),
                    account_id uuid,
                    created_on timestamptz DEFAULT now(),
                    created_by uuid,
                    owner_id uuid,
                    description text,
                    subject text,
                    number serial,
                    closed_on timestamptz,
                    l_scope text NOT NULL DEFAULT '',
                    priority text DEFAULT 'low',
                    status_id uuid,
                    type_id uuid,
                    x_search text DEFAULT '',
                    last_modified_on timestamptz,
                    last_modified_by uuid,
                    CONSTRAINT pk_rec_case PRIMARY KEY (id)
                );
            ");

            // -------------------------------------------------------
            // 3.4 rec_address — Address entity
            //     Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0
            //     Source: NextPlugin.20190204.cs (lines 1897-2120)
            //     Cross-service refs (NO FK): country_id, created_by,
            //       last_modified_by
            // -------------------------------------------------------
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

            // ============================================================
            // STEP 4: Create Many-to-Many join tables
            //         Source: NextPlugin.20190204.cs relation definitions
            // ============================================================

            // -------------------------------------------------------
            // 4.1 rel_account_nn_contact — Account <-> Contact M:N
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_account_nn_contact (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_account_nn_contact PRIMARY KEY (origin_id, target_id)
                );
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_account_nn_contact_origin ON rel_account_nn_contact (origin_id);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_account_nn_contact_target ON rel_account_nn_contact (target_id);");

            // -------------------------------------------------------
            // 4.2 rel_account_nn_case — Account <-> Case M:N
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_account_nn_case (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_account_nn_case PRIMARY KEY (origin_id, target_id)
                );
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_account_nn_case_origin ON rel_account_nn_case (origin_id);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_account_nn_case_target ON rel_account_nn_case (target_id);");

            // -------------------------------------------------------
            // 4.3 rel_address_nn_account — Address <-> Account M:N
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS rel_address_nn_account (
                    origin_id uuid NOT NULL,
                    target_id uuid NOT NULL,
                    CONSTRAINT pk_rel_address_nn_account PRIMARY KEY (origin_id, target_id)
                );
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_address_nn_account_origin ON rel_address_nn_account (origin_id);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_address_nn_account_target ON rel_address_nn_account (target_id);");

            // ============================================================
            // STEP 5: Create intra-service FK constraints
            // ============================================================

            // FK: rec_case.status_id -> rec_case_status.id
            // Source: NextPlugin.20190204.cs (case_status_1n_case relation)
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_name = 'fk_case_status_1n_case'
                          AND table_name = 'rec_case'
                    ) THEN
                        ALTER TABLE rec_case ADD CONSTRAINT fk_case_status_1n_case
                            FOREIGN KEY (status_id) REFERENCES rec_case_status(id);
                    END IF;
                END $$;
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_case_status_1n_case ON rec_case (status_id);");

            // FK: rec_case.type_id -> rec_case_type.id
            // Source: NextPlugin.20190204.cs (case_type_1n_case relation)
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_name = 'fk_case_type_1n_case'
                          AND table_name = 'rec_case'
                    ) THEN
                        ALTER TABLE rec_case ADD CONSTRAINT fk_case_type_1n_case
                            FOREIGN KEY (type_id) REFERENCES rec_case_type(id);
                    END IF;
                END $$;
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_case_type_1n_case ON rec_case (type_id);");

            // FK: rec_account.salutation_id -> rec_salutation.id
            // Source: NextPlugin.20190206.cs (salutation_1n_account relation, line 1334)
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_name = 'fk_salutation_1n_account'
                          AND table_name = 'rec_account'
                    ) THEN
                        ALTER TABLE rec_account ADD CONSTRAINT fk_salutation_1n_account
                            FOREIGN KEY (salutation_id) REFERENCES rec_salutation(id);
                    END IF;
                END $$;
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_salutation_1n_account ON rec_account (salutation_id);");

            // FK: rec_contact.salutation_id -> rec_salutation.id
            // Source: NextPlugin.20190206.cs (salutation_1n_contact relation, line 1363)
            migrationBuilder.Sql(@"
                DO $$ BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.table_constraints
                        WHERE constraint_name = 'fk_salutation_1n_contact'
                          AND table_name = 'rec_contact'
                    ) THEN
                        ALTER TABLE rec_contact ADD CONSTRAINT fk_salutation_1n_contact
                            FOREIGN KEY (salutation_id) REFERENCES rec_salutation(id);
                    END IF;
                END $$;
            ");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_r_salutation_1n_contact ON rec_contact (salutation_id);");

            // ============================================================
            // STEP 6: Create indexes for searchable and commonly queried fields
            // ============================================================

            // Searchable fields (x_search columns used for text search)
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_account_x_search ON rec_account (x_search);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_contact_x_search ON rec_contact (x_search);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_case_x_search ON rec_case (x_search);");

            // Commonly queried entity fields
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_account_name ON rec_account (name);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_contact_email ON rec_contact (email);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_case_subject ON rec_case (subject);");

            // Cross-service reference indexes (efficient lookup, no FK)
            // Account cross-service refs
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_account_country_id ON rec_account (country_id);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_account_language_id ON rec_account (language_id);");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_account_currency_id ON rec_account (currency_id);");

            // Contact cross-service ref
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_contact_country_id ON rec_contact (country_id);");

            // Address cross-service ref
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS idx_rec_address_country_id ON rec_address (country_id);");

            // ============================================================
            // STEP 7: Seed reference/lookup data
            //         All GUIDs are exact from source patch files.
            //         Uses ON CONFLICT DO NOTHING for idempotency.
            // ============================================================

            // -------------------------------------------------------
            // 7.1 Seed rec_case_status — 9 records
            //     Source: NextPlugin.20190203.cs (lines 5393-5579)
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                INSERT INTO rec_case_status (id, is_default, label, sort_index, is_closed, is_system, is_enabled, l_scope, icon_class, color)
                VALUES
                    ('4f17785b-c430-4fea-9fa9-8cfef931c60e', true,  'Open',               1.0,   false, true, true, '', NULL, NULL),
                    ('c04d2a73-9fd3-4d00-b32e-9887e517f3bf', false, 'Closed - Duplicate',  103.0, true,  true, true, '', NULL, NULL),
                    ('b7368bd9-ea1c-4091-8c57-26e5c8360c29', false, 'Closed - No Response', 102.0, true,  true, true, '', NULL, NULL),
                    ('2aac0c08-5e84-477d-add0-5bc60057eba4', false, 'Closed - Resolved',   100.0, true,  true, true, '', NULL, NULL),
                    ('61cba6d4-b175-4a89-94b6-6b700ce9adb9', false, 'Closed - Rejected',   101.0, true,  true, true, '', NULL, NULL),
                    ('fe9d8d44-996a-4e8a-8448-3d7731d4f278', false, 'Re-Open',             10.0,  false, true, true, '', NULL, NULL),
                    ('508d9e1b-8896-46ed-a6fd-734197bdb1c8', false, 'Wait for Customer',   50.0,  false, true, true, '', NULL, NULL),
                    ('95170be2-dcd9-4399-9ac4-7ecefb67ad2d', false, 'Escalated',           52.0,  false, true, true, '', NULL, NULL),
                    ('ef18bf1e-314e-472f-887b-e348daef9676', false, 'On Hold',             40.0,  false, true, true, '', NULL, NULL)
                ON CONFLICT (id) DO NOTHING;
            ");

            // -------------------------------------------------------
            // 7.2 Seed rec_case_type — 5 records
            //     Source: NextPlugin.20190203.cs (lines 5582-5679)
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                INSERT INTO rec_case_type (id, is_default, is_enabled, is_system, l_scope, sort_index, label, icon_class, color)
                VALUES
                    ('3298c9b3-560b-48b2-b148-997f9cbb3bec', true,  true, true, '', 1.0, 'General',         NULL, NULL),
                    ('f228d073-bd09-48ed-85c7-54c6231c9182', false, true, true, '', 2.0, 'Problem',         NULL, NULL),
                    ('92b35547-f91b-492d-9c83-c29c3a4d132d', false, true, true, '', 3.0, 'Question',        NULL, NULL),
                    ('15e7adc5-a3e7-47c5-ae54-252cffe82923', false, true, true, '', 4.0, 'Feature Request', NULL, NULL),
                    ('dc4b7e9f-0790-47b5-a89c-268740aded38', false, true, true, '', 5.0, 'Duplicate',       NULL, NULL)
                ON CONFLICT (id) DO NOTHING;
            ");

            // -------------------------------------------------------
            // 7.3 Seed rec_salutation — 5 records
            //     Source: NextPlugin.20190206.cs (lines 1212-1299)
            // -------------------------------------------------------
            migrationBuilder.Sql(@"
                INSERT INTO rec_salutation (id, is_default, is_enabled, is_system, label, sort_index, l_scope)
                VALUES
                    ('87c08ee1-8d4d-4c89-9b37-4e3cc3f98698', true,  true, true, 'Mr.',   1.0, ''),
                    ('0ede7d96-2d85-45fa-818b-01327d4c47a9', false, true, true, 'Ms.',   2.0, ''),
                    ('ab073457-ddc8-4d36-84a5-38619528b578', false, true, true, 'Mrs.',  3.0, ''),
                    ('5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1', false, true, true, 'Dr.',   4.0, ''),
                    ('a74cd934-b425-4061-8f4e-a6d6b9d7adb1', false, true, true, 'Prof.', 5.0, '')
                ON CONFLICT (id) DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Reverse all operations in REVERSE order of creation.
            // DO NOT drop the uuid-ossp extension (may be shared).
            // ============================================================

            // -------------------------------------------------------
            // STEP 1: Drop FK constraints first
            // -------------------------------------------------------
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_contact DROP CONSTRAINT IF EXISTS fk_salutation_1n_contact;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_account DROP CONSTRAINT IF EXISTS fk_salutation_1n_account;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_case DROP CONSTRAINT IF EXISTS fk_case_type_1n_case;");
            migrationBuilder.Sql(@"ALTER TABLE IF EXISTS rec_case DROP CONSTRAINT IF EXISTS fk_case_status_1n_case;");

            // -------------------------------------------------------
            // STEP 2: Drop indexes on entity tables (explicit cleanup
            //         for any indexes not auto-dropped with tables)
            // -------------------------------------------------------
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_r_salutation_1n_contact;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_r_salutation_1n_account;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_r_case_type_1n_case;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_r_case_status_1n_case;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_address_country_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_contact_country_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_account_currency_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_account_language_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_account_country_id;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_case_subject;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_contact_email;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_account_name;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_case_x_search;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_contact_x_search;");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS idx_rec_account_x_search;");

            // -------------------------------------------------------
            // STEP 3: Drop join tables
            // -------------------------------------------------------
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_address_nn_account;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_account_nn_case;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rel_account_nn_contact;");

            // -------------------------------------------------------
            // STEP 4: Drop entity tables in reverse creation order
            // -------------------------------------------------------
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_address;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_contact;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_account;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_salutation;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case_type;");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS rec_case_status;");
        }
    }
}
