using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace WebVella.Erp.Service.Core.Database.Migrations
{
    /// <summary>
    /// Initial EF Core migration for the Core Platform Service (erp_core database).
    /// Codifies the COMPLETE cumulative schema from the monolith's CheckCreateSystemTables()
    /// method in ERPService.cs (lines 922-1438) plus DDL from DbRepository.CreatePostgresqlExtensions()
    /// and DbRepository.CreatePostgresqlCasts() (lines 13-46).
    /// Replaces the monolith's date-based ProcessPatches() migration system with EF Core
    /// __EFMigrationsHistory tracking.
    /// </summary>
    /// <inheritdoc />
    [Migration("20250101000000_InitialCreate")]
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // Phase 1: PostgreSQL Extensions
            // Source: WebVella.Erp/Database/DbRepository.cs,
            //         CreatePostgresqlExtensions() (lines 26-46)
            // ================================================================

            // uuid-ossp extension for UUID generation functions
            migrationBuilder.Sql(
                @"CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";");

            // PostGIS extension — optional, wrapped in exception handler
            // to gracefully handle environments where PostGIS is not installed
            migrationBuilder.Sql(@"
DO $$
BEGIN
    CREATE EXTENSION IF NOT EXISTS ""postgis"";
EXCEPTION WHEN OTHERS THEN
    NULL;
END $$;");

            // ================================================================
            // Phase 2: PostgreSQL Implicit Casts (text/varchar → uuid)
            // Source: WebVella.Erp/Database/DbRepository.cs,
            //         CreatePostgresqlCasts() (lines 13-24)
            // ================================================================

            migrationBuilder.Sql(@"
DROP CAST IF EXISTS(varchar AS uuid);
DROP CAST IF EXISTS(text AS uuid);
CREATE CAST(text AS uuid) WITH INOUT AS IMPLICIT;
CREATE CAST(varchar AS uuid) WITH INOUT AS IMPLICIT;");

            // ================================================================
            // Phase 3: Core System Tables
            // Source: WebVella.Erp/ERPService.cs,
            //         CheckCreateSystemTables() (lines 922-1210)
            // ================================================================

            // ------------------------------------------------------------------
            // Table 1: entities
            // Stores entity definitions as JSON documents.
            // Source: ERPService.cs line 937
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.entities (
    id uuid NOT NULL,
    ""json"" json NOT NULL,
    CONSTRAINT entities_pkey PRIMARY KEY (id)
) WITH (OIDS = FALSE);");

            // ------------------------------------------------------------------
            // Table 2: entity_relations
            // Stores entity relation definitions as JSON documents.
            // Source: ERPService.cs line 952
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.entity_relations (
    id uuid NOT NULL,
    ""json"" json NOT NULL,
    CONSTRAINT entity_relations_pkey PRIMARY KEY (id)
) WITH (OIDS = FALSE);");

            // ------------------------------------------------------------------
            // Table 3: system_settings
            // Stores system-level configuration including schema version.
            // Source: ERPService.cs line 968
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.system_settings (
    id uuid NOT NULL,
    version integer NOT NULL,
    CONSTRAINT system_settings_pkey PRIMARY KEY (id)
) WITH (OIDS = FALSE);");

            // ------------------------------------------------------------------
            // Table 4: system_search
            // Full-text search index table for cross-entity search.
            // Source: ERPService.cs lines 984-1003
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
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
    CONSTRAINT system_search_pkey PRIMARY KEY (id)
)
WITH (oids = false);");

            // GIN index for PostgreSQL full-text search on stemmed content
            migrationBuilder.Sql(
                @"CREATE INDEX system_search_fts_idx ON system_search USING gin(to_tsvector('english', stem_content));");

            // ------------------------------------------------------------------
            // Table 5: files
            // File metadata for large-object, filesystem, and cloud storage.
            // Source: ERPService.cs lines 1018-1046
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.files (
    id uuid NOT NULL,
    object_id numeric(18) NOT NULL,
    filepath text NOT NULL,
    created_on timestamp WITHOUT TIME ZONE NOT NULL,
    modified_on timestamp WITHOUT TIME ZONE NOT NULL,
    created_by uuid,
    modified_by uuid,
    CONSTRAINT files_pkey PRIMARY KEY (id),
    CONSTRAINT udx_filepath UNIQUE (filepath),
    CONSTRAINT udx_object_id UNIQUE (object_id)
) WITH (OIDS = FALSE);");

            // Unique index on filepath for fast lookups
            // Source: ERPService.cs line 1040
            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX idx_filepath ON files (filepath);");

            // Drop udx_object_id unique constraint immediately after creation
            // to support filesystem storage where object_id is 0 for all files.
            // Source: ERPService.cs line 1046
            migrationBuilder.Sql(
                @"ALTER TABLE public.files DROP CONSTRAINT udx_object_id;");

            // ------------------------------------------------------------------
            // Table 6: jobs
            // Background job execution tracking.
            // Source: ERPService.cs lines 1061-1087
            // Includes cumulative 'result' column (line 1143).
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.jobs (
    id uuid NOT NULL,
    type_id uuid NOT NULL,
    type_name text NOT NULL,
    complete_class_name text NOT NULL,
    attributes text,
    status integer NOT NULL,
    priority integer NOT NULL,
    started_on timestamp WITH TIME ZONE,
    finished_on timestamp WITH TIME ZONE,
    aborted_by uuid,
    canceled_by uuid,
    error_message text,
    schedule_plan_id uuid,
    created_on timestamp WITH TIME ZONE NOT NULL,
    last_modified_on timestamp WITH TIME ZONE NOT NULL,
    created_by uuid,
    last_modified_by uuid,
    result text,
    CONSTRAINT jobs_pkey PRIMARY KEY (id)
) WITH (OIDS = FALSE);");

            // ------------------------------------------------------------------
            // Table 7: schedule_plan
            // Scheduled job execution plans (interval, daily, etc.).
            // Source: ERPService.cs lines 1101-1128
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.schedule_plan (
    id uuid NOT NULL,
    name text NOT NULL,
    type integer NOT NULL,
    start_date timestamp WITH TIME ZONE,
    end_date timestamp WITH TIME ZONE,
    schedule_days json,
    interval_in_minutes integer,
    start_timespan integer,
    end_timespan integer,
    last_trigger_time timestamp WITH TIME ZONE,
    next_trigger_time timestamp WITH TIME ZONE,
    job_type_id uuid NOT NULL,
    job_attributes text,
    enabled boolean NOT NULL,
    last_started_job_id uuid,
    created_on timestamp WITH TIME ZONE NOT NULL,
    last_modified_on timestamp WITH TIME ZONE NOT NULL,
    last_modified_by uuid,
    CONSTRAINT schedule_plan_pkey PRIMARY KEY (id)
) WITH (OIDS = FALSE);");

            // ------------------------------------------------------------------
            // Table 8: system_log
            // Database-backed diagnostic/audit logging.
            // Source: ERPService.cs lines 1159-1186
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.system_log (
    id UUID NOT NULL,
    created_on TIMESTAMP WITH TIME ZONE DEFAULT '2011-11-11 02:11:11+02'::timestamp with time zone NOT NULL,
    type INTEGER DEFAULT 1 NOT NULL,
    message TEXT DEFAULT 'message'::text NOT NULL,
    source TEXT DEFAULT 'source'::text NOT NULL,
    details TEXT,
    notification_status INTEGER DEFAULT 1 NOT NULL,
    CONSTRAINT system_log_pkey PRIMARY KEY (id)
)
WITH (oids = false);");

            // system_log indexes (5 indexes, lines 1171-1184)
            migrationBuilder.Sql(
                @"CREATE INDEX idx_system_log_created_on ON public.system_log USING btree (created_on);");
            migrationBuilder.Sql(
                @"CREATE INDEX idx_system_log_message ON public.system_log USING btree (message COLLATE pg_catalog.""default"");");
            migrationBuilder.Sql(
                @"CREATE INDEX idx_system_log_notification_status ON public.system_log USING btree (notification_status);");
            migrationBuilder.Sql(
                @"CREATE INDEX idx_system_log_source ON public.system_log USING btree (source COLLATE pg_catalog.""default"");");
            migrationBuilder.Sql(
                @"CREATE INDEX idx_system_log_type ON public.system_log USING btree (type);");

            // ------------------------------------------------------------------
            // Table 9: plugin_data
            // Stores plugin-specific configuration and state as JSON.
            // Source: ERPService.cs lines 1201-1208
            // Note: No public. prefix — preserved from source.
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE plugin_data (
    id UUID NOT NULL,
    name TEXT DEFAULT ''::text NOT NULL,
    data TEXT DEFAULT ''::text,
    CONSTRAINT idx_u_plugin_data_name UNIQUE (name),
    CONSTRAINT plugin_data_pkey PRIMARY KEY (id)
)
WITH (oids = false);");

            // ================================================================
            // Phase 4: App / Sitemap / Page Schema
            // Source: ERPService.cs lines 1225-1418
            //         + upgrade columns from UpdateSitemapNodeTable1/2
            //           (lines 1445-1465)
            // All cumulative columns included in initial CREATE TABLE.
            // ================================================================

            // ------------------------------------------------------------------
            // Table 10: app
            // Application definitions with role-based access control.
            // Source: ERPService.cs lines 1225-1236
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app (
    id uuid NOT NULL,
    name text DEFAULT ''::text NOT NULL,
    label text NOT NULL,
    description text,
    icon_class text,
    author text,
    color text,
    weight integer DEFAULT '-1'::integer NOT NULL,
    access uuid[]
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 11: app_sitemap_area
            // Application sitemap area definitions.
            // Source: ERPService.cs lines 1238-1252
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_sitemap_area (
    id uuid NOT NULL,
    name text DEFAULT ''::text NOT NULL,
    label text,
    label_translations text,
    description text,
    description_translations text,
    icon_class text,
    weight integer DEFAULT '-1'::integer NOT NULL,
    color text,
    show_group_names boolean DEFAULT false NOT NULL,
    access_roles uuid[] NOT NULL,
    app_id uuid NOT NULL
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 12: app_sitemap_area_group
            // Groupings within sitemap areas.
            // Source: ERPService.cs lines 1254-1263
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_sitemap_area_group (
    id uuid NOT NULL,
    area_id uuid NOT NULL,
    weight integer DEFAULT '-1'::integer NOT NULL,
    name text NOT NULL,
    label text,
    label_translations text,
    render_roles uuid[] NOT NULL
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 13: app_sitemap_area_node
            // Sitemap navigation nodes (cumulative: includes entity_*_pages
            // columns from UpdateSitemapNodeTable1 and parent_id from
            // UpdateSitemapNodeTable2).
            // Source: ERPService.cs lines 1265-1278 + 1445-1465
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_sitemap_area_node (
    id uuid NOT NULL,
    area_id uuid NOT NULL,
    name text NOT NULL,
    label text,
    label_translations text,
    icon_class text,
    url text,
    weight integer NOT NULL,
    access_roles uuid[] NOT NULL,
    type integer NOT NULL,
    entity_id uuid,
    entity_list_pages uuid[] NOT NULL DEFAULT array[]::uuid[],
    entity_create_pages uuid[] NOT NULL DEFAULT array[]::uuid[],
    entity_details_pages uuid[] NOT NULL DEFAULT array[]::uuid[],
    entity_manage_pages uuid[] NOT NULL DEFAULT array[]::uuid[],
    parent_id uuid DEFAULT NULL
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 14: app_page
            // Page definitions (cumulative: includes layout column from
            // line 1380-1381).
            // Source: ERPService.cs lines 1280-1296 + 1380-1381
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_page (
    id uuid NOT NULL,
    name text NOT NULL,
    label text,
    icon_class text,
    system boolean DEFAULT false,
    type integer NOT NULL,
    weight integer DEFAULT '-1'::integer NOT NULL,
    label_translations text,
    razor_body text,
    area_id uuid,
    node_id uuid,
    app_id uuid,
    entity_id uuid,
    is_razor_body boolean DEFAULT false NOT NULL,
    layout text NOT NULL DEFAULT ''
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 15: app_page_body_node
            // Page body component tree nodes (cumulative: includes container_id
            // column from line 1310-1311).
            // Source: ERPService.cs lines 1298-1311
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_page_body_node (
    id uuid NOT NULL,
    parent_id uuid,
    node_id uuid,
    page_id uuid NOT NULL,
    weight integer DEFAULT '-1'::integer NOT NULL,
    component_name text,
    options text,
    container_id text
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Indexes for app tables
            // Source: ERPService.cs lines 1313-1318
            // ------------------------------------------------------------------
            migrationBuilder.Sql(
                @"CREATE INDEX idx_app_page_body_node_page_id ON app_page_body_node USING btree (page_id);");
            migrationBuilder.Sql(
                @"CREATE INDEX idx_app_page_app_id ON app_page USING btree (app_id);");
            migrationBuilder.Sql(
                @"CREATE INDEX fki_app_page_body_node_parent_id ON app_page_body_node USING btree (parent_id);");
            migrationBuilder.Sql(
                @"CREATE INDEX fki_app_page_area_id ON app_page USING btree (area_id);");
            migrationBuilder.Sql(
                @"CREATE INDEX fki_app_page_node_id ON app_page USING btree (node_id);");
            migrationBuilder.Sql(
                @"CREATE INDEX fki_app_page_entity_id ON app_page USING btree (entity_id);");

            // ------------------------------------------------------------------
            // Primary Key constraints for app tables
            // Source: ERPService.cs lines 1320-1342
            // Note: App tables are created without inline PKs (matching source
            // pattern), PKs added via ALTER TABLE.
            // ------------------------------------------------------------------
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page_body_node ADD CONSTRAINT app_page_body_node_pkey PRIMARY KEY (id);");
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app ADD CONSTRAINT app_pkey PRIMARY KEY (id);");
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area ADD CONSTRAINT app_sitemap_area_pkey PRIMARY KEY (id);");
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area_group ADD CONSTRAINT app_sitemap_area_group_pkey PRIMARY KEY (id);");
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area_node ADD CONSTRAINT app_sitemap_area_node_pkey PRIMARY KEY (id);");
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page ADD CONSTRAINT app_page_pkey PRIMARY KEY (id);");

            // ------------------------------------------------------------------
            // Foreign Key constraints for app tables
            // Source: ERPService.cs lines 1344-1378
            // ------------------------------------------------------------------

            // app_page_body_node self-referencing FK (parent_id → id)
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page_body_node ADD CONSTRAINT fkey_app_page_body_node_parent_id FOREIGN KEY (parent_id) REFERENCES app_page_body_node(id);");

            // app_page_body_node → app_page
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page_body_node ADD CONSTRAINT fkey_app_page_body_node_page_id FOREIGN KEY (page_id) REFERENCES app_page(id);");

            // app_sitemap_area → app
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area ADD CONSTRAINT fkey_app_id FOREIGN KEY (app_id) REFERENCES app(id);");

            // app_sitemap_area_group → app_sitemap_area
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area_group ADD CONSTRAINT fkey_area_id FOREIGN KEY (area_id) REFERENCES app_sitemap_area(id);");

            // app_sitemap_area_node → app_sitemap_area
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_sitemap_area_node ADD CONSTRAINT fkey_area_id FOREIGN KEY (area_id) REFERENCES app_sitemap_area(id);");

            // app_page → app
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page ADD CONSTRAINT fkey_app_id FOREIGN KEY (app_id) REFERENCES app(id);");

            // app_page → app_sitemap_area
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page ADD CONSTRAINT fkey_area_id FOREIGN KEY (area_id) REFERENCES app_sitemap_area(id);");

            // app_page → app_sitemap_area_node
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app_page ADD CONSTRAINT fkey_node_id FOREIGN KEY (node_id) REFERENCES app_sitemap_area_node(id);");

            // app unique constraint on name
            // Source: ERPService.cs lines 1376-1378
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY app ADD CONSTRAINT ux_app_name UNIQUE (name);");

            // app_sitemap_area_node self-referencing FK (parent_id → id)
            // Source: ERPService.cs UpdateSitemapNodeTable2, lines 1463-1465
            migrationBuilder.Sql(
                @"ALTER TABLE ONLY public.app_sitemap_area_node ADD CONSTRAINT fkey_app_sitemap_area_node_parent_id FOREIGN KEY (parent_id) REFERENCES app_sitemap_area_node(id);");

            // ------------------------------------------------------------------
            // Table 16: data_source
            // EQL/SQL data source definitions (cumulative: includes return_total
            // column from line 1435).
            // Source: ERPService.cs lines 1383-1396 + 1435
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.data_source (
    id UUID NOT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    weight INTEGER NOT NULL,
    eql_text TEXT NOT NULL,
    sql_text TEXT NOT NULL,
    parameters_json TEXT NOT NULL,
    fields_json TEXT NOT NULL,
    entity_name TEXT NOT NULL,
    return_total boolean NOT NULL DEFAULT true,
    CONSTRAINT data_source_pkey PRIMARY KEY (id),
    CONSTRAINT ux_data_source_name UNIQUE (name)
)
WITH (oids = false);");

            // ------------------------------------------------------------------
            // Table 17: app_page_data_source
            // Links data sources to pages with parameter overrides.
            // Source: ERPService.cs lines 1399-1418
            // ------------------------------------------------------------------
            migrationBuilder.Sql(@"
CREATE TABLE public.app_page_data_source (
    parameters TEXT NOT NULL,
    name TEXT NOT NULL,
    id UUID NOT NULL,
    page_id UUID NOT NULL,
    data_source_id UUID NOT NULL,
    CONSTRAINT app_page_data_source_pkey PRIMARY KEY (id),
    CONSTRAINT app_page_data_uxc_name_page_id UNIQUE (name, page_id)
)
WITH (oids = false);");

            // FK: app_page_data_source → app_page
            migrationBuilder.Sql(@"
ALTER TABLE public.app_page_data_source
    ADD CONSTRAINT fkey_page_id FOREIGN KEY (page_id)
    REFERENCES public.app_page(id)
    ON DELETE NO ACTION
    ON UPDATE NO ACTION
    NOT DEFERRABLE;");

            // Index on page_id for FK lookups
            migrationBuilder.Sql(
                @"CREATE INDEX fki_app_page_data_fkc_page_id ON public.app_page_data_source USING btree (page_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ================================================================
            // Drop all tables in reverse dependency order.
            // Uses CASCADE to handle any remaining FK references gracefully.
            // ================================================================

            // Tables with FK dependencies dropped first
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_page_data_source CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_page_body_node CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_page CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_sitemap_area_node CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_sitemap_area_group CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app_sitemap_area CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.app CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.data_source CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.plugin_data CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.system_log CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.schedule_plan CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.jobs CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.files CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.system_search CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.system_settings CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.entity_relations CASCADE;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS public.entities CASCADE;");

            // Drop implicit casts
            migrationBuilder.Sql("DROP CAST IF EXISTS(text AS uuid);");
            migrationBuilder.Sql("DROP CAST IF EXISTS(varchar AS uuid);");

            // Drop extensions
            migrationBuilder.Sql(@"DROP EXTENSION IF EXISTS ""uuid-ossp"";");
        }
    }
}
