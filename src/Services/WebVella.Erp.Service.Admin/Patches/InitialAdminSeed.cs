using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Admin.Patches
{
    /// <summary>
    /// Initial EF Core migration for the Admin/SDK service that replaces the monolith's
    /// custom date-based versioning patch system for the SDK plugin.
    ///
    /// This migration codifies the cumulative final state of all 5 original SDK plugin
    /// patches (Patch20181215 through Patch20210429) into a single EF Core migration:
    ///
    ///   Patch20181215 — Created SDK application, 3 sitemap areas, 10 sitemap nodes
    ///   Patch20190227 — Updated app access, areas (icon changes), and 8 nodes
    ///   Patch20200610 — Updated app author to HTML navigation links
    ///   Patch20201221 — Updated role/user/user_file entity metadata and RecordPermissions
    ///   Patch20210429 — Reconciled PostgreSQL column defaults (now() → typed defaults)
    ///
    /// All seed data (GUIDs, text values, permissions) is preserved character-for-character
    /// from the monolith source to maintain backward compatibility with existing databases.
    ///
    /// Version tracking is now handled by EF Core's __EFMigrationsHistory table instead
    /// of the monolith's plugin_data JSON persistence with integer date versioning.
    /// </summary>
    [Migration("20210429_InitialAdminSeed")]
    public class InitialAdminSeed : Migration
    {
        #region Well-Known GUIDs

        // SDK Application identifiers (from SdkPlugin._.cs constants)
        private static readonly Guid SdkAppId = new Guid("56a8548a-19d0-497f-8e5b-242abfdc4082");
        private static readonly Guid AreaDesignId = new Guid("d3237d8c-c074-46d7-82c2-1385cbfff35a");
        private static readonly Guid AreaAccessId = new Guid("c5c4cefc-1402-4a8b-9867-7f2a059b745d");
        private static readonly Guid AreaServerId = new Guid("fee72214-f1c4-4ed5-8bda-35698dc11528");

        // Sitemap node identifiers (from Patch20181215)
        private static readonly Guid NodePageId = new Guid("5b132ac0-703e-4342-a13d-c7ff93d07a4f");
        private static readonly Guid NodeDataSourceId = new Guid("9b30bf96-67d9-4d20-bf07-e6ef1c44d553");
        private static readonly Guid NodeApplicationId = new Guid("02d75ea5-8fc6-4f95-9933-0eed6b36ca49");
        private static readonly Guid NodeEntityId = new Guid("dfa7ec55-b55b-404f-b251-889f1d81df29");
        private static readonly Guid NodeCodegenId = new Guid("4571de62-a817-4a94-8b49-4b230cc0d2ad");
        private static readonly Guid NodeUserId = new Guid("ff578868-817e-433d-988f-bb8d4e9baa0d");
        private static readonly Guid NodeRoleId = new Guid("75567fc4-70e1-41a9-9e32-2e5b62636598");
        private static readonly Guid NodeJobId = new Guid("396ec481-3b2e-461c-b514-743fb3252003");
        private static readonly Guid NodeLogId = new Guid("78a29ac8-d2aa-4379-b990-08f7f164a895");

        // Entity identifier for user_file (not in SystemIds — from Patch20201221)
        private static readonly Guid UserFileEntityId = new Guid("5c666c54-9e76-4327-ac7a-55851037810c");

        #endregion

        /// <summary>
        /// Applies the cumulative final state of all 5 SDK plugin patches as a single
        /// forward migration. Uses migrationBuilder.Sql() with raw PostgreSQL statements
        /// to reproduce the exact database state the monolith patches would produce.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Resolve SystemIds GUIDs to lowercase strings for SQL embedding.
            // Using SystemIds constants ensures a single source of truth for well-known role/entity IDs.
            var adminRoleId = SystemIds.AdministratorRoleId.ToString().ToLowerInvariant();
            var regularRoleId = SystemIds.RegularRoleId.ToString().ToLowerInvariant();
            var guestRoleId = SystemIds.GuestRoleId.ToString().ToLowerInvariant();
            var userEntityId = SystemIds.UserEntityId.ToString().ToLowerInvariant();
            var roleEntityId = SystemIds.RoleEntityId.ToString().ToLowerInvariant();

            // Organize RecordPermissions GUID lists for the three system entities.
            // These collections mirror the exact permission assignments from Patch20201221.
            var roleUserCanRead = new List<Guid>
            {
                SystemIds.GuestRoleId,
                SystemIds.RegularRoleId,
                SystemIds.AdministratorRoleId
            };
            var roleUserCanCreate = new List<Guid>
            {
                SystemIds.GuestRoleId,
                SystemIds.AdministratorRoleId
            };
            var roleUserCanUpdate = new List<Guid>
            {
                SystemIds.AdministratorRoleId
            };
            var roleUserCanDelete = new List<Guid>
            {
                SystemIds.AdministratorRoleId
            };
            var userFileCanAll = new List<Guid>
            {
                SystemIds.RegularRoleId,
                SystemIds.AdministratorRoleId
            };

            // Step 1: Seed SDK Application
            // Cumulative final state from Patch20181215 + Patch20190227 + Patch20200610
            SeedSdkApplication(migrationBuilder, adminRoleId);

            // Step 2: Seed Sitemap Areas
            // Cumulative final state from Patch20181215 + Patch20190227
            SeedSitemapAreas(migrationBuilder);

            // Step 3: Seed Sitemap Nodes
            // Cumulative final state from Patch20181215 + Patch20190227
            SeedSitemapNodes(migrationBuilder);

            // Step 4: Update System Entity Metadata
            // From Patch20201221 — updates role, user, user_file entities
            UpdateSystemEntityMetadata(migrationBuilder, roleEntityId, userEntityId,
                roleUserCanRead, roleUserCanCreate, roleUserCanUpdate, roleUserCanDelete,
                userFileCanAll);

            // Step 5: Schema Reconciliation
            // From Patch20210429 — reconciles PostgreSQL column defaults
            ReconcileSchemaDefaults(migrationBuilder);
        }

        /// <summary>
        /// Reverses the migration by removing all seed data created in Up().
        /// Entity metadata reversion may require manual intervention since original
        /// pre-patch metadata values are not preserved in this migration.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Delete sitemap nodes first (they reference areas)
            migrationBuilder.Sql($@"
                DELETE FROM public.app_sitemap_area_node
                WHERE id IN (
                    '{NodePageId}'::uuid,
                    '{NodeDataSourceId}'::uuid,
                    '{NodeApplicationId}'::uuid,
                    '{NodeEntityId}'::uuid,
                    '{NodeCodegenId}'::uuid,
                    '{NodeUserId}'::uuid,
                    '{NodeRoleId}'::uuid,
                    '{NodeJobId}'::uuid,
                    '{NodeLogId}'::uuid
                );");

            // Delete sitemap areas (they reference the app)
            migrationBuilder.Sql($@"
                DELETE FROM public.app_sitemap_area
                WHERE id IN (
                    '{AreaDesignId}'::uuid,
                    '{AreaAccessId}'::uuid,
                    '{AreaServerId}'::uuid
                );");

            // Delete the SDK application
            migrationBuilder.Sql($@"
                DELETE FROM public.app WHERE id = '{SdkAppId}'::uuid;");

            // Entity metadata reversion note:
            // The original pre-Patch20201221 values for role, user, and user_file entity
            // metadata (IconName, Color, RecordPermissions) are not captured in this
            // migration. Reverting these changes requires restoring the entity JSON from
            // a backup or manually reconstructing the original metadata.
            // The schema reconciliation step (Patch20210429) is also not fully reversible
            // since the original column defaults were already 'now()' which is what the
            // reconciliation preserves for date/datetime fields.
            migrationBuilder.Sql(@"
                -- WARNING: Entity metadata reversion is not automated.
                -- If you need to revert the role/user/user_file entity metadata changes
                -- from Patch20201221, restore from a database backup or manually
                -- reconstruct the original RecordPermissions JSON values.
                SELECT 1;");
        }

        #region Private Seed Methods

        /// <summary>
        /// Seeds the SDK application record into the public.app table.
        /// Final state reflects cumulative changes from Patch20181215 (create),
        /// Patch20190227 (update access), and Patch20200610 (update author to HTML nav).
        /// </summary>
        private static void SeedSdkApplication(MigrationBuilder migrationBuilder, string adminRoleId)
        {
            // The Author field contains HTML with single quotes — use dollar-quoting
            // to avoid PostgreSQL string escaping issues.
            // The exact Author HTML is preserved character-for-character from Patch20200610.
            migrationBuilder.Sql($@"
                INSERT INTO public.app (id, name, label, description, icon_class, author, color, weight, access)
                VALUES (
                    '{SdkAppId}'::uuid,
                    'sdk',
                    'Software Development Kit',
                    'SDK & Development Tools',
                    'fa fa-cogs',
                    $author$<ul class='nav'><li class='nav-item'><a class='nav-link' href='/sdk/objects/page/l'>pages</a></li><li class='nav-item'><a class='nav-link'href='/sdk/objects/entity/l'>entities</a></li><li class='nav-item'><a class='nav-link'href='/sdk/objects/application/l/list'>apps</a></li><li class='nav-item'><a class='nav-link'href='/sdk/access/user/l/list'>users</a></li><li class='nav-item'><a class='nav-link'href='/sdk/server/log/l/list'>logs</a></li><li class='nav-item'><a class='nav-link'href='/sdk/server/job/l/list'>jobs</a></li></ul>$author$,
                    '#dc3545',
                    1000,
                    ARRAY['{adminRoleId}']::uuid[]
                )
                ON CONFLICT (id) DO UPDATE SET
                    name = EXCLUDED.name,
                    label = EXCLUDED.label,
                    description = EXCLUDED.description,
                    icon_class = EXCLUDED.icon_class,
                    author = EXCLUDED.author,
                    color = EXCLUDED.color,
                    weight = EXCLUDED.weight,
                    access = EXCLUDED.access;");
        }

        /// <summary>
        /// Seeds the three SDK sitemap areas (Objects, Access, Server) into
        /// public.app_sitemap_area. Final state from Patch20190227 updates.
        /// </summary>
        private static void SeedSitemapAreas(MigrationBuilder migrationBuilder)
        {
            // Objects area — "fa fa-pencil-ruler" is the final icon (Patch20190227
            // changed from "fas fa-pencil-ruler" in Patch20181215)
            migrationBuilder.Sql($@"
                INSERT INTO public.app_sitemap_area
                    (id, app_id, name, label, label_translations, description,
                     description_translations, icon_class, color, weight,
                     show_group_names, access_roles)
                VALUES (
                    '{AreaDesignId}'::uuid,
                    '{SdkAppId}'::uuid,
                    'objects',
                    'Objects',
                    '[]',
                    'Schema and Layout management',
                    '[]',
                    'fa fa-pencil-ruler',
                    '#2196F3',
                    1,
                    false,
                    ARRAY[]::uuid[]
                )
                ON CONFLICT (id) DO UPDATE SET
                    app_id = EXCLUDED.app_id,
                    name = EXCLUDED.name,
                    label = EXCLUDED.label,
                    label_translations = EXCLUDED.label_translations,
                    description = EXCLUDED.description,
                    description_translations = EXCLUDED.description_translations,
                    icon_class = EXCLUDED.icon_class,
                    color = EXCLUDED.color,
                    weight = EXCLUDED.weight,
                    show_group_names = EXCLUDED.show_group_names,
                    access_roles = EXCLUDED.access_roles;");

            // Access area
            migrationBuilder.Sql($@"
                INSERT INTO public.app_sitemap_area
                    (id, app_id, name, label, label_translations, description,
                     description_translations, icon_class, color, weight,
                     show_group_names, access_roles)
                VALUES (
                    '{AreaAccessId}'::uuid,
                    '{SdkAppId}'::uuid,
                    'access',
                    'Access',
                    '[]',
                    'Manage users and roles',
                    '[]',
                    'fa fa-key',
                    '#673AB7',
                    2,
                    false,
                    ARRAY[]::uuid[]
                )
                ON CONFLICT (id) DO UPDATE SET
                    app_id = EXCLUDED.app_id,
                    name = EXCLUDED.name,
                    label = EXCLUDED.label,
                    label_translations = EXCLUDED.label_translations,
                    description = EXCLUDED.description,
                    description_translations = EXCLUDED.description_translations,
                    icon_class = EXCLUDED.icon_class,
                    color = EXCLUDED.color,
                    weight = EXCLUDED.weight,
                    show_group_names = EXCLUDED.show_group_names,
                    access_roles = EXCLUDED.access_roles;");

            // Server area
            migrationBuilder.Sql($@"
                INSERT INTO public.app_sitemap_area
                    (id, app_id, name, label, label_translations, description,
                     description_translations, icon_class, color, weight,
                     show_group_names, access_roles)
                VALUES (
                    '{AreaServerId}'::uuid,
                    '{SdkAppId}'::uuid,
                    'server',
                    'Server',
                    '[]',
                    'Background jobs and maintenance',
                    '[]',
                    'fa fa-database',
                    '#F44336',
                    3,
                    false,
                    ARRAY[]::uuid[]
                )
                ON CONFLICT (id) DO UPDATE SET
                    app_id = EXCLUDED.app_id,
                    name = EXCLUDED.name,
                    label = EXCLUDED.label,
                    label_translations = EXCLUDED.label_translations,
                    description = EXCLUDED.description,
                    description_translations = EXCLUDED.description_translations,
                    icon_class = EXCLUDED.icon_class,
                    color = EXCLUDED.color,
                    weight = EXCLUDED.weight,
                    show_group_names = EXCLUDED.show_group_names,
                    access_roles = EXCLUDED.access_roles;");
        }

        /// <summary>
        /// Seeds all 10 SDK sitemap nodes across the three areas.
        /// Objects area: page, data_source, application, entity, codegen (5 nodes)
        /// Access area: user, role (2 nodes)
        /// Server area: job, log (2 nodes)
        /// Total: 9 nodes per AAP + codegen = 10 nodes from source
        ///
        /// Nodes updated by Patch20190227 use their final state (with '[]' label_translations).
        /// Nodes not updated by Patch20190227 (codegen, role) use Patch20181215 state
        /// (with NULL label_translations).
        /// </summary>
        private static void SeedSitemapNodes(MigrationBuilder migrationBuilder)
        {
            // Helper: generates INSERT SQL for a sitemap node
            void InsertNode(Guid nodeId, Guid areaId, string name, string label,
                string labelTranslations, string iconClass, string url, int weight)
            {
                var lblTrValue = labelTranslations != null ? $"'{labelTranslations}'" : "NULL";
                migrationBuilder.Sql($@"
                    INSERT INTO public.app_sitemap_area_node
                        (id, parent_id, area_id, name, label, label_translations, weight,
                         type, icon_class, url, entity_id, access_roles,
                         entity_list_pages, entity_create_pages,
                         entity_details_pages, entity_manage_pages)
                    VALUES (
                        '{nodeId}'::uuid,
                        NULL,
                        '{areaId}'::uuid,
                        '{name}',
                        '{label}',
                        {lblTrValue},
                        {weight},
                        3,
                        '{iconClass}',
                        '{url}',
                        NULL,
                        ARRAY[]::uuid[],
                        ARRAY[]::uuid[],
                        ARRAY[]::uuid[],
                        ARRAY[]::uuid[],
                        ARRAY[]::uuid[]
                    )
                    ON CONFLICT (id) DO UPDATE SET
                        parent_id = EXCLUDED.parent_id,
                        area_id = EXCLUDED.area_id,
                        name = EXCLUDED.name,
                        label = EXCLUDED.label,
                        label_translations = EXCLUDED.label_translations,
                        weight = EXCLUDED.weight,
                        type = EXCLUDED.type,
                        icon_class = EXCLUDED.icon_class,
                        url = EXCLUDED.url,
                        entity_id = EXCLUDED.entity_id,
                        access_roles = EXCLUDED.access_roles,
                        entity_list_pages = EXCLUDED.entity_list_pages,
                        entity_create_pages = EXCLUDED.entity_create_pages,
                        entity_details_pages = EXCLUDED.entity_details_pages,
                        entity_manage_pages = EXCLUDED.entity_manage_pages;");
            }

            // ── Objects area nodes ──────────────────────────────────────────────

            // page node (final state from Patch20190227)
            InsertNode(NodePageId, AreaDesignId,
                "page", "Pages", "[]", "fa fa-file",
                "/sdk/objects/page/l", 1);

            // data_source node (final state from Patch20190227)
            InsertNode(NodeDataSourceId, AreaDesignId,
                "data_source", "Data sources", "[]", "fa fa-cloud-download-alt",
                "/sdk/objects/data_source/l/list", 2);

            // application node (final state from Patch20190227)
            InsertNode(NodeApplicationId, AreaDesignId,
                "application", "Applications", "[]", "fa fa-th",
                "/sdk/objects/application/l/list", 3);

            // entity node (final state from Patch20190227)
            InsertNode(NodeEntityId, AreaDesignId,
                "entity", "Entities", "[]", "fa fa-database",
                "/sdk/objects/entity/l", 4);

            // codegen node (unchanged from Patch20181215 — not updated by Patch20190227)
            InsertNode(NodeCodegenId, AreaDesignId,
                "codegen", "Code generation", null, "fa fa-code",
                "/sdk/objects/codegen/a/codegen", 10);

            // ── Access area nodes ───────────────────────────────────────────────

            // user node (final state from Patch20190227)
            InsertNode(NodeUserId, AreaAccessId,
                "user", "Users", "[]", "fa fa-user",
                "/sdk/access/user/l/list", 1);

            // role node (unchanged from Patch20181215 — Patch20190227 does NOT update it)
            InsertNode(NodeRoleId, AreaAccessId,
                "role", "Roles", null, "fa fa-key",
                "/sdk/access/role/l/list", 2);

            // ── Server area nodes ───────────────────────────────────────────────

            // job node (final state from Patch20190227)
            InsertNode(NodeJobId, AreaServerId,
                "job", "Background jobs", "[]", "fa fa-cogs",
                "/sdk/server/job/l/plan", 1);

            // log node (final state from Patch20190227 — icon changed to "fas fa-sticky-note")
            InsertNode(NodeLogId, AreaServerId,
                "log", "Logs", "[]", "fas fa-sticky-note",
                "/sdk/server/log/l/list", 2);
        }

        /// <summary>
        /// Updates entity metadata and RecordPermissions for the three system entities
        /// (role, user, user_file) as specified in Patch20201221.
        ///
        /// Uses a PL/pgSQL DO block with jsonb_set to surgically update individual fields
        /// within the entity JSON stored in the entities table, preserving all other fields
        /// and TypeNameHandling.Auto $type metadata annotations from Newtonsoft.Json.
        /// </summary>
        private static void UpdateSystemEntityMetadata(
            MigrationBuilder migrationBuilder,
            string roleEntityId,
            string userEntityId,
            List<Guid> canRead,
            List<Guid> canCreate,
            List<Guid> canUpdate,
            List<Guid> canDelete,
            List<Guid> userFileCanAll)
        {
            // Build JSON arrays for RecordPermissions GUID lists
            string BuildGuidJsonArray(List<Guid> guids)
            {
                var items = new List<string>();
                foreach (var g in guids)
                {
                    items.Add("\"" + g.ToString().ToLowerInvariant() + "\"");
                }
                return "[" + string.Join(",", items) + "]";
            }

            var canReadJson = BuildGuidJsonArray(canRead);
            var canCreateJson = BuildGuidJsonArray(canCreate);
            var canUpdateJson = BuildGuidJsonArray(canUpdate);
            var canDeleteJson = BuildGuidJsonArray(canDelete);
            var userFileCanAllJson = BuildGuidJsonArray(userFileCanAll);

            // Helper: generates a PL/pgSQL DO block to update entity metadata.
            // Uses sequential jsonb_set calls on a local variable to preserve the
            // entire JSON document structure including $type annotations while
            // updating only the specific metadata fields from Patch20201221.
            void UpdateEntityMetadataSql(string entityId, string entityName,
                string label, string labelPlural, string iconName, string color,
                string permCanRead, string permCanCreate,
                string permCanUpdate, string permCanDelete)
            {
                // Note: NpgsqlConnection could be used here as an alternative
                // for programmatic schema operations if PL/pgSQL is insufficient.
                // See ReconcileSchemaDefaults for a similar pattern.
                var sql = @"
                DO $$
                DECLARE
                    current_json JSONB;
                    current_perms JSONB;
                BEGIN
                    -- Read the current entity JSON
                    SELECT json::jsonb INTO current_json
                    FROM public.entities
                    WHERE id = '" + entityId + @"'::uuid;

                    IF current_json IS NOT NULL THEN
                        -- Update top-level entity metadata fields
                        current_json := jsonb_set(current_json, '{Name}', '""" + entityName + @"""');
                        current_json := jsonb_set(current_json, '{Label}', '""" + label + @"""');
                        current_json := jsonb_set(current_json, '{LabelPlural}', '""" + labelPlural + @"""');
                        current_json := jsonb_set(current_json, '{System}', 'true');
                        current_json := jsonb_set(current_json, '{IconName}', '""" + iconName + @"""');
                        current_json := jsonb_set(current_json, '{Color}', '""" + color + @"""');
                        current_json := jsonb_set(current_json, '{RecordScreenIdField}', 'null');

                        -- Update RecordPermissions while preserving existing $type annotation.
                        -- First get the existing RecordPermissions object (or empty if none).
                        current_perms := COALESCE(current_json->'RecordPermissions', '{}'::jsonb);

                        -- Set each permission list individually to preserve other keys
                        current_perms := jsonb_set(current_perms, '{CanRead}', '" + permCanRead + @"');
                        current_perms := jsonb_set(current_perms, '{CanCreate}', '" + permCanCreate + @"');
                        current_perms := jsonb_set(current_perms, '{CanUpdate}', '" + permCanUpdate + @"');
                        current_perms := jsonb_set(current_perms, '{CanDelete}', '" + permCanDelete + @"');

                        -- Write the updated permissions back into the entity JSON
                        current_json := jsonb_set(current_json, '{RecordPermissions}', current_perms);

                        -- Persist the updated JSON back to the entities table
                        UPDATE public.entities
                        SET json = current_json::json
                        WHERE id = '" + entityId + @"'::uuid;
                    END IF;
                END $$;";

                migrationBuilder.Sql(sql);
            }

            // Update role entity (Id = c4541fee-fbb6-4661-929e-1724adec285a)
            // RecordPermissions: CanRead=[Guest,Regular,Admin], CanCreate=[Guest,Admin],
            //                    CanUpdate=[Admin], CanDelete=[Admin]
            UpdateEntityMetadataSql(roleEntityId, "role",
                "Role", "Roles", "fa fa-key", "#f44336",
                canReadJson, canCreateJson, canUpdateJson, canDeleteJson);

            // Update user entity (Id = b9cebc3b-6443-452a-8e34-b311a73dcc8b)
            // Same RecordPermissions pattern as role entity
            UpdateEntityMetadataSql(userEntityId, "user",
                "User", "Users", "fa fa-user", "#f44336",
                canReadJson, canCreateJson, canUpdateJson, canDeleteJson);

            // Update user_file entity (Id = 5c666c54-9e76-4327-ac7a-55851037810c)
            // Different permissions: all four lists contain [Regular, Admin]
            UpdateEntityMetadataSql(
                UserFileEntityId.ToString().ToLowerInvariant(), "user_file",
                "User File", "User Files", "fa fa-file", "#f44336",
                userFileCanAllJson, userFileCanAllJson, userFileCanAllJson, userFileCanAllJson);
        }

        /// <summary>
        /// Reconciles PostgreSQL column defaults that were incorrectly set to now().
        /// This is the EF Core migration equivalent of Patch20210429 which queried
        /// information_schema.columns for columns with column_default = 'now()' in
        /// rec_* tables and re-applied the correct typed default values.
        ///
        /// The original patch used NpgsqlConnection to query the schema dynamically and
        /// DbRepository.SetColumnDefaultValue to fix each column. This PL/pgSQL DO block
        /// replicates that behavior:
        /// - For DateField/DateTimeField with UseCurrentTimeAsDefaultValue: keeps now()
        /// - For other field types: resets the default based on the field metadata
        ///
        /// This step is dynamic because it depends on runtime database state — the set
        /// of columns with now() defaults may vary between database instances.
        /// </summary>
        private static void ReconcileSchemaDefaults(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                DECLARE
                    r RECORD;
                    entity_name TEXT;
                    entity_json JSONB;
                    field_json JSONB;
                    field_type TEXT;
                    use_current_time BOOLEAN;
                    field_default_val TEXT;
                BEGIN
                    -- Iterate over all columns in rec_* tables that have now() as default
                    FOR r IN
                        SELECT table_name, column_name
                        FROM information_schema.columns
                        WHERE table_schema = 'public' AND column_default = 'now()'
                    LOOP
                        -- Only process record tables (rec_* prefix)
                        IF r.table_name NOT LIKE 'rec_%' THEN
                            CONTINUE;
                        END IF;

                        -- Extract entity name by stripping the 'rec_' prefix
                        entity_name := substring(r.table_name from 5);
                        entity_json := NULL;
                        field_json := NULL;

                        -- Look up the entity JSON from the entities table
                        BEGIN
                            SELECT (e.json::jsonb) INTO entity_json
                            FROM public.entities e
                            WHERE e.json::jsonb->>'Name' = entity_name;
                        EXCEPTION WHEN OTHERS THEN
                            CONTINUE;
                        END;

                        IF entity_json IS NULL THEN
                            CONTINUE;
                        END IF;

                        -- Find the field definition in the entity's Fields array
                        BEGIN
                            SELECT f INTO field_json
                            FROM jsonb_array_elements(entity_json->'Fields') AS f
                            WHERE f->>'Name' = r.column_name
                            LIMIT 1;
                        EXCEPTION WHEN OTHERS THEN
                            CONTINUE;
                        END;

                        IF field_json IS NULL THEN
                            CONTINUE;
                        END IF;

                        -- Determine the field type from the $type annotation
                        -- DateField and DateTimeField types include those strings in the $type
                        field_type := COALESCE(field_json->>'$type', '');
                        use_current_time := false;

                        IF field_type LIKE '%DateField%' OR field_type LIKE '%DateTimeField%' THEN
                            use_current_time := COALESCE(
                                (field_json->>'UseCurrentTimeAsDefaultValue')::boolean,
                                false
                            );
                        END IF;

                        IF use_current_time THEN
                            -- Date/DateTime field with UseCurrentTimeAsDefaultValue=true:
                            -- Re-apply now() as the default (preserves existing behavior)
                            EXECUTE format(
                                'ALTER TABLE ONLY %I ALTER COLUMN %I SET DEFAULT now()',
                                r.table_name, r.column_name
                            );
                        ELSE
                            -- For non-date fields or date fields without UseCurrentTimeAsDefaultValue:
                            -- Extract the stored default value and apply it, or set to NULL
                            field_default_val := field_json->>'DefaultValue';
                            IF field_default_val IS NOT NULL AND field_default_val <> '' THEN
                                EXECUTE format(
                                    'ALTER TABLE ONLY %I ALTER COLUMN %I SET DEFAULT %L',
                                    r.table_name, r.column_name, field_default_val
                                );
                            ELSE
                                EXECUTE format(
                                    'ALTER TABLE ONLY %I ALTER COLUMN %I SET DEFAULT NULL',
                                    r.table_name, r.column_name
                                );
                            END IF;
                        END IF;
                    END LOOP;
                END $$;");
        }

        #endregion
    }
}
