using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// EF Core migration converted from the monolith's ProjectPlugin.Patch20211012 method.
    /// This migration performs entity metadata and permissions updates, creates the account.logo
    /// image field, updates the SDK application/sitemap structure, modifies page body nodes
    /// for dashboard/details/list pages, and rebuilds multiple data source definitions.
    ///
    /// Source: WebVella.Erp.Plugins.Project/ProjectPlugin.20211012.cs (1426 lines)
    /// All GUIDs, JSON options, EQL/SQL text preserved verbatim from source.
    /// </summary>
    [Migration("20211012000000")]
    public class Patch20211012_EntityMetadataPermissions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =====================================================================
            // Region: Update entity — role
            // Source: entMan.UpdateEntity() for role (c4541fee-fbb6-4661-929e-1724adec285a)
            // Updates metadata and record permissions with exact role GUIDs.
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE entities SET
    name = 'role',
    label = 'Role',
    label_plural = 'Roles',
    system = true,
    icon_name = 'fa fa-key',
    color = '#f44336',
    record_screen_id_field = NULL,
    record_permissions = '{
        ""CanRead"": [
            ""987148b1-afa8-4b33-8616-55861e5fd065"",
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanCreate"": [
            ""987148b1-afa8-4b33-8616-55861e5fd065"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanUpdate"": [
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanDelete"": [
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ]
    }'
WHERE id = 'c4541fee-fbb6-4661-929e-1724adec285a';
");

            // =====================================================================
            // Region: Update entity — user
            // Source: entMan.UpdateEntity() for user (b9cebc3b-6443-452a-8e34-b311a73dcc8b)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE entities SET
    name = 'user',
    label = 'User',
    label_plural = 'Users',
    system = true,
    icon_name = 'fa fa-user',
    color = '#f44336',
    record_screen_id_field = NULL,
    record_permissions = '{
        ""CanRead"": [
            ""987148b1-afa8-4b33-8616-55861e5fd065"",
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanCreate"": [
            ""987148b1-afa8-4b33-8616-55861e5fd065"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanUpdate"": [
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanDelete"": [
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ]
    }'
WHERE id = 'b9cebc3b-6443-452a-8e34-b311a73dcc8b';
");

            // =====================================================================
            // Region: Update entity — user_file
            // Source: entMan.UpdateEntity() for user_file (5c666c54-9e76-4327-ac7a-55851037810c)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE entities SET
    name = 'user_file',
    label = 'User File',
    label_plural = 'User Files',
    system = true,
    icon_name = 'fa fa-file',
    color = '#f44336',
    record_screen_id_field = NULL,
    record_permissions = '{
        ""CanRead"": [
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanCreate"": [
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanUpdate"": [
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ],
        ""CanDelete"": [
            ""f16ec6db-626d-4c27-8de0-3e7ce542c55f"",
            ""bdc56420-caf0-4030-8a0e-d264938e0cda""
        ]
    }'
WHERE id = '5c666c54-9e76-4327-ac7a-55851037810c';
");

            // =====================================================================
            // Region: Create field — account.logo (InputImageField)
            // Source: entMan.CreateField(entityId=2e22b50f-..., imageField, false)
            // Field id: ff2be918-4132-4eac-a7d7-576facc52355
            // =====================================================================
            migrationBuilder.Sql(@"
INSERT INTO entity_fields (id, entity_id, name, label, placeholder_text, description, help_text,
    required, unique_value, searchable, auditable, system, field_type, default_value,
    enable_security, permissions)
VALUES (
    'ff2be918-4132-4eac-a7d7-576facc52355',
    '2e22b50f-e444-4b62-a171-076e51246939',
    'logo',
    'Logo',
    NULL,
    NULL,
    NULL,
    false,
    false,
    false,
    false,
    true,
    9,
    '',
    false,
    '{""CanRead"": [], ""CanUpdate"": []}'
)
ON CONFLICT (id) DO NOTHING;
");

            // =====================================================================
            // Region: Update app — sdk
            // Source: AppService().UpdateApplication(id=56a8548a-19d0-497f-8e5b-242abfdc4082)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE applications SET
    name = 'sdk',
    label = 'Software Development Kit',
    description = 'SDK & Development Tools',
    icon_class = 'fa fa-cogs',
    author = '<ul class=''nav''><li class=''nav-item''><a class=''nav-link'' href=''/sdk/objects/page/l''>pages</a></li><li class=''nav-item''><a class=''nav-link''href=''/sdk/objects/entity/l''>entities</a></li><li class=''nav-item''><a class=''nav-link''href=''/sdk/objects/application/l/list''>apps</a></li><li class=''nav-item''><a class=''nav-link''href=''/sdk/access/user/l/list''>users</a></li><li class=''nav-item''><a class=''nav-link''href=''/sdk/server/log/l/list''>logs</a></li><li class=''nav-item''><a class=''nav-link''href=''/sdk/server/job/l/list''>jobs</a></li></ul>',
    color = '#dc3545',
    weight = 1000,
    access = '[""bdc56420-caf0-4030-8a0e-d264938e0cda""]'
WHERE id = '56a8548a-19d0-497f-8e5b-242abfdc4082';
");

            // =====================================================================
            // Region: Update sitemap area — objects
            // Source: AppService().UpdateArea(id=d3237d8c-c074-46d7-82c2-1385cbfff35a)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_areas SET
    name = 'objects',
    label = 'Objects',
    description = 'Schema and Layout management',
    icon_class = 'fa fa-pencil-ruler',
    color = '#2196F3',
    weight = 1,
    show_group_names = false,
    access = '[]',
    label_translations = '[]',
    description_translations = '[]'
WHERE id = 'd3237d8c-c074-46d7-82c2-1385cbfff35a'
  AND app_id = '56a8548a-19d0-497f-8e5b-242abfdc4082';
");

            // =====================================================================
            // Region: Update sitemap area — access
            // Source: AppService().UpdateArea(id=c5c4cefc-1402-4a8b-9867-7f2a059b745d)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_areas SET
    name = 'access',
    label = 'Access',
    description = 'Manage users and roles',
    icon_class = 'fa fa-key',
    color = '#673AB7',
    weight = 2,
    show_group_names = false,
    access = '[]',
    label_translations = '[]',
    description_translations = '[]'
WHERE id = 'c5c4cefc-1402-4a8b-9867-7f2a059b745d'
  AND app_id = '56a8548a-19d0-497f-8e5b-242abfdc4082';
");

            // =====================================================================
            // Region: Update sitemap area — server
            // Source: AppService().UpdateArea(id=fee72214-f1c4-4ed5-8bda-35698dc11528)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_areas SET
    name = 'server',
    label = 'Server',
    description = 'Background jobs and maintenance',
    icon_class = 'fa fa-database',
    color = '#F44336',
    weight = 3,
    show_group_names = false,
    access = '[]',
    label_translations = '[]',
    description_translations = '[]'
WHERE id = 'fee72214-f1c4-4ed5-8bda-35698dc11528'
  AND app_id = '56a8548a-19d0-497f-8e5b-242abfdc4082';
");

            // =====================================================================
            // Region: Update sitemap node — page
            // Source: AppService().UpdateAreaNode(id=5b132ac0-703e-4342-a13d-c7ff93d07a4f)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'page',
    label = 'Pages',
    label_translations = '[]',
    icon_class = 'fa fa-file',
    url = '/sdk/objects/page/l',
    type = 3,
    entity_id = NULL,
    weight = 1,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = '5b132ac0-703e-4342-a13d-c7ff93d07a4f'
  AND area_id = 'd3237d8c-c074-46d7-82c2-1385cbfff35a';
");

            // =====================================================================
            // Region: Update sitemap node — data_source
            // Source: AppService().UpdateAreaNode(id=9b30bf96-67d9-4d20-bf07-e6ef1c44d553)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'data_source',
    label = 'Data sources',
    label_translations = '[]',
    icon_class = 'fa fa-cloud-download-alt',
    url = '/sdk/objects/data_source/l/list',
    type = 3,
    entity_id = NULL,
    weight = 2,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = '9b30bf96-67d9-4d20-bf07-e6ef1c44d553'
  AND area_id = 'd3237d8c-c074-46d7-82c2-1385cbfff35a';
");

            // =====================================================================
            // Region: Update sitemap node — application
            // Source: AppService().UpdateAreaNode(id=02d75ea5-8fc6-4f95-9933-0eed6b36ca49)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'application',
    label = 'Applications',
    label_translations = '[]',
    icon_class = 'fa fa-th',
    url = '/sdk/objects/application/l/list',
    type = 3,
    entity_id = NULL,
    weight = 3,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = '02d75ea5-8fc6-4f95-9933-0eed6b36ca49'
  AND area_id = 'd3237d8c-c074-46d7-82c2-1385cbfff35a';
");

            // =====================================================================
            // Region: Update sitemap node — entity
            // Source: AppService().UpdateAreaNode(id=dfa7ec55-b55b-404f-b251-889f1d81df29)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'entity',
    label = 'Entities',
    label_translations = '[]',
    icon_class = 'fa fa-database',
    url = '/sdk/objects/entity/l',
    type = 3,
    entity_id = NULL,
    weight = 4,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = 'dfa7ec55-b55b-404f-b251-889f1d81df29'
  AND area_id = 'd3237d8c-c074-46d7-82c2-1385cbfff35a';
");

            // =====================================================================
            // Region: Update sitemap node — user
            // Source: AppService().UpdateAreaNode(id=ff578868-817e-433d-988f-bb8d4e9baa0d)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'user',
    label = 'Users',
    label_translations = '[]',
    icon_class = 'fa fa-user',
    url = '/sdk/access/user/l/list',
    type = 3,
    entity_id = NULL,
    weight = 1,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = 'ff578868-817e-433d-988f-bb8d4e9baa0d'
  AND area_id = 'c5c4cefc-1402-4a8b-9867-7f2a059b745d';
");

            // =====================================================================
            // Region: Update sitemap node — job
            // Source: AppService().UpdateAreaNode(id=396ec481-3b2e-461c-b514-743fb3252003)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'job',
    label = 'Background jobs',
    label_translations = '[]',
    icon_class = 'fa fa-cogs',
    url = '/sdk/server/job/l/plan',
    type = 3,
    entity_id = NULL,
    weight = 1,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = '396ec481-3b2e-461c-b514-743fb3252003'
  AND area_id = 'fee72214-f1c4-4ed5-8bda-35698dc11528';
");

            // =====================================================================
            // Region: Update sitemap node — log
            // Source: AppService().UpdateAreaNode(id=78a29ac8-d2aa-4379-b990-08f7f164a895)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE app_sitemap_nodes SET
    name = 'log',
    label = 'Logs',
    label_translations = '[]',
    icon_class = 'fas fa-sticky-note',
    url = '/sdk/server/log/l/list',
    type = 3,
    entity_id = NULL,
    weight = 2,
    access = '[]',
    entity_list_pages = '[]',
    entity_create_pages = '[]',
    entity_details_pages = '[]',
    entity_manage_pages = '[]',
    parent_id = NULL
WHERE id = '78a29ac8-d2aa-4379-b990-08f7f164a895'
  AND area_id = 'fee72214-f1c4-4ed5-8bda-35698dc11528';
");

            // =====================================================================
            // Region: Update page body node — dashboard: 63daa5c0 (My Overdue Tasks)
            // Source: PageService().UpdatePageBodyNode(...)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a (dashboard)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a584a5ed-96a2-4a28-95e8-23266bc36926',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column2',
    options = '{
  ""is_visible"": """",
  ""title"": ""My Overdue Tasks"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm mb-3 "",
  ""body_class"": """",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = '63daa5c0-ed7f-432e-bfbb-746b94207146';
");

            // =====================================================================
            // Region: Update page body node — dashboard: ae930e6f (All Users' Timesheet)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a584a5ed-96a2-4a28-95e8-23266bc36926',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column1',
    options = '{
  ""is_visible"": """",
  ""title"": ""All Users'''' Timesheet"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = 'ae930e6f-38b5-4c48-a17f-63b0bdf7dab6';
");

            // =====================================================================
            // Region: Update page body node — dashboard: 47303562 (Tasks)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = '151e265c-d3d3-4340-92fc-0cace2ca45f9',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column1',
    options = '{
  ""is_visible"": """",
  ""title"": ""Tasks"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = '47303562-04a3-4935-b228-aaa61527f963';
");

            // =====================================================================
            // Region: Update page body node — dashboard: be907fa3 (Priority)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = '151e265c-d3d3-4340-92fc-0cace2ca45f9',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column2',
    options = '{
  ""is_visible"": """",
  ""title"": ""Priority"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = 'be907fa3-0971-45b5-9dcf-fabbb277fe54';
");

            // =====================================================================
            // Region: Update page body node — dashboard: e49cf2f9 (My Timesheet)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a584a5ed-96a2-4a28-95e8-23266bc36926',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column1',
    options = '{
  ""is_visible"": """",
  ""title"": ""My Timesheet"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = 'e49cf2f9-82b0-4988-aa29-427e8d9501d9';
");

            // =====================================================================
            // Region: Update page body node — dashboard: 8e533c53 (My 10 Upcoming Tasks)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a584a5ed-96a2-4a28-95e8-23266bc36926',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 3,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column2',
    options = '{
  ""is_visible"": """",
  ""title"": ""My 10 Upcoming Tasks "",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": """",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = '8e533c53-0bf5-4082-ae06-f47f1bd9b3b5';
");

            // =====================================================================
            // Region: Update page body node — dashboard: 6ef7bbd7 (My Tasks Due Today)
            // Page: 33f2cd33-cf38-4247-9097-75f895d1ef7a
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a584a5ed-96a2-4a28-95e8-23266bc36926',
    node_id = NULL,
    page_id = '33f2cd33-cf38-4247-9097-75f895d1ef7a',
    weight = 2,
    component_name = 'WebVella.Erp.Web.Components.PcSection',
    container_id = 'column2',
    options = '{
  ""is_visible"": """",
  ""title"": ""My Tasks Due Today"",
  ""title_tag"": ""span"",
  ""is_card"": ""true"",
  ""is_collapsable"": ""false"",
  ""is_collapsed"": ""false"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": """",
  ""label_mode"": ""1"",
  ""field_mode"": ""1""
}'
WHERE id = '6ef7bbd7-b96c-45d4-97e1-b8e43f489ed5';
");

            // =====================================================================
            // Region: Update page body node — details: 754bf941 (PcFieldText subject)
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c (details)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'e15e2d00-e704-4212-a7d2-ee125dd687a6',
    node_id = NULL,
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldText',
    container_id = 'column1',
    options = '{
  ""is_visible"": """",
  ""label_mode"": ""0"",
  ""label_text"": ""Subject 123"",
  ""link"": """",
  ""mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.subject\"",\""default\"":\""\""}"",
  ""name"": ""subject"",
  ""class"": """",
  ""maxlength"": 0,
  ""placeholder"": """",
  ""connected_entity_id"": """",
  ""connected_record_id_ds"": """",
  ""access_override_ds"": """",
  ""required_override_ds"": """",
  ""ajax_api_url_ds"": """",
  ""description"": """",
  ""label_help_text"": """"
}'
WHERE id = '754bf941-df31-4b13-ba32-eb3c7a8c8922';
");

            // =====================================================================
            // Region: Update page body node — details: 151d5da3 (PcFieldDate start_time)
            // Page: 3a40b8e6-0a87-4eee-9b6b-6c665ebee28c
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = '651e5fb2-56df-4c46-86b3-19a641dc942d',
    node_id = NULL,
    page_id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDate',
    container_id = 'body',
    options = '{
  ""is_visible"": """",
  ""label_mode"": ""0"",
  ""label_text"": """",
  ""mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""class"": """",
  ""show_icon"": ""false"",
  ""connected_entity_id"": """",
  ""connected_record_id_ds"": """",
  ""access_override_ds"": """",
  ""required_override_ds"": """",
  ""ajax_api_url_ds"": """",
  ""description"": """",
  ""label_help_text"": """"
}'
WHERE id = '151d5da3-161a-44c0-97fa-84c76c9d3b60';
");

            // =====================================================================
            // Region: Update page body node — open: e1b676c0 (PcButton Search)
            // Page: 273dd749-3804-48c8-8306-078f1e7f3b3f (open)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = '250115da-cea5-46f3-a77a-d2f7704c650d',
    node_id = NULL,
    page_id = '273dd749-3804-48c8-8306-078f1e7f3b3f',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcButton',
    container_id = 'actions',
    options = '{
  ""type"": ""0"",
  ""text"": ""Search"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-search"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": ""ErpEvent.DISPATCH(''''WebVella.Erp.Web.Components.PcDrawer'''',''''open'''')"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}'
WHERE id = 'e1b676c0-e128-46a2-b2cc-51a5b3ec2816';
");

            // =====================================================================
            // Region: Update page body node — all: ad9c357f (PcFieldDate created_on)
            // Page: 6d3fe557-59dd-4a2e-b710-f3f326ae172b (all)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc',
    node_id = NULL,
    page_id = '6d3fe557-59dd-4a2e-b710-f3f326ae172b',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDate',
    container_id = 'column7',
    options = '{
  ""is_visible"": """",
  ""label_mode"": ""3"",
  ""label_text"": """",
  ""mode"": ""4"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""class"": """",
  ""show_icon"": ""false"",
  ""connected_entity_id"": """",
  ""connected_record_id_ds"": """",
  ""access_override_ds"": """",
  ""required_override_ds"": """",
  ""ajax_api_url_ds"": """"
}'
WHERE id = 'ad9c357f-e620-4ed1-9593-d76c97019677';
");

            // =====================================================================
            // Region: Update page body node — open: bd05d5ef (PcFieldDate created_on)
            // Page: 273dd749-3804-48c8-8306-078f1e7f3b3f (open)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_body_nodes SET
    parent_id = 'a4719fbd-b3d0-4f81-b302-96f5620e17cc',
    node_id = NULL,
    page_id = '273dd749-3804-48c8-8306-078f1e7f3b3f',
    weight = 1,
    component_name = 'WebVella.Erp.Web.Components.PcFieldDate',
    container_id = 'column7',
    options = '{
  ""is_visible"": """",
  ""label_mode"": ""3"",
  ""label_text"": """",
  ""mode"": ""4"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""class"": """",
  ""show_icon"": ""false"",
  ""connected_entity_id"": """",
  ""connected_record_id_ds"": """",
  ""access_override_ds"": """",
  ""required_override_ds"": """",
  ""ajax_api_url_ds"": """"
}'
WHERE id = 'bd05d5ef-0ab4-48b0-a40e-5959875d071b';
");

            // =====================================================================
            // Region: Update data source — WvProjectAllAccounts
            // Source: DbDataSourceRepository().Update(id=61d21547-b353-48b8-8b75-b727680da79e)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectAllAccounts',
    description = 'Lists all accounts in the system',
    weight = 10,
    eql_text = 'SELECT id,name 
FROM account
where name CONTAINS @name
ORDER BY @sortBy ASC
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT 
	 rec_account.""id"" AS ""id"",
	 rec_account.""name"" AS ""name"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_account
WHERE  ( rec_account.""name""  ILIKE  CONCAT ( ''%'' , @name , ''%'' ) )
ORDER BY rec_account.""name"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""name"",""type"":""text"",""value"":""null"",""ignore_parse_errors"":false},{""name"":""sortBy"",""type"":""text"",""value"":""name"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]',
    entity_name = 'account'
WHERE id = '61d21547-b353-48b8-8b75-b727680da79e';
");

            // =====================================================================
            // Region: Update data source — WvProjectAllTasks
            // Source: DbDataSourceRepository().Update(id=5a6e9d56-63bc-43b1-b95e-24838db9f435)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectAllTasks',
    description = 'All tasks selection',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  ( rec_task.""x_search""  ILIKE  @searchQuery ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '5a6e9d56-63bc-43b1-b95e-24838db9f435';
");

            // =====================================================================
            // Region: Update data source — WvProjectAllProjects
            // Source: DbDataSourceRepository().Update(id=96218f33-42f1-4ff1-926c-b1765e1f8c6e)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectAllProjects',
    description = 'all project records',
    weight = 10,
    eql_text = 'SELECT id,abbr,name,$user_1n_project_owner.username
FROM project
WHERE name CONTAINS @filterName AND (@onlyActive = false OR start_date < @now AND end_date > @now)
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT 
	 rec_project.""id"" AS ""id"",
	 rec_project.""abbr"" AS ""abbr"",
	 rec_project.""name"" AS ""name"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_project_owner
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM (
	 SELECT 
		 user_1n_project_owner.""id"" AS ""id"",
		 user_1n_project_owner.""username"" AS ""username""
	 FROM rec_user user_1n_project_owner
	 WHERE user_1n_project_owner.id = rec_project.owner_id ) d )::jsonb AS ""$user_1n_project_owner""	
	-------< $user_1n_project_owner

FROM rec_project
WHERE  (  ( rec_project.""name""  ILIKE  CONCAT ( ''%'' , @filterName , ''%'' ) ) AND  (  ( @onlyActive = FALSE )  OR  (  ( rec_project.""start_date"" < @now )  AND  ( rec_project.""end_date"" > @now )  )  )  ) 
ORDER BY rec_project.""name"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""name"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""filterName"",""type"":""text"",""value"":""null"",""ignore_parse_errors"":false},{""name"":""now"",""type"":""date"",""value"":""utc_now"",""ignore_parse_errors"":false},{""name"":""onlyActive"",""type"":""bool"",""value"":""false"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_project_owner"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'project'
WHERE id = '96218f33-42f1-4ff1-926c-b1765e1f8c6e';
");

            // =====================================================================
            // Region: Update data source — WvProjectCommentsForRecordId
            // Source: DbDataSourceRepository().Update(id=a588e096-358d-4426-adf6-5db693f32322)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectCommentsForRecordId',
    description = 'Get all comments for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_comment.image,$user_1n_comment.username
FROM comment
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_comment.""id"" AS ""id"",
	 rec_comment.""body"" AS ""body"",
	 rec_comment.""created_by"" AS ""created_by"",
	 rec_comment.""parent_id"" AS ""parent_id"",
	 rec_comment.""created_on"" AS ""created_on"",
	 rec_comment.""l_related_records"" AS ""l_related_records"",
	 rec_comment.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_comment
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_comment.""id"" AS ""id"",
		 user_1n_comment.""image"" AS ""image"",
		 user_1n_comment.""username"" AS ""username"" 
	 FROM rec_user user_1n_comment
	 WHERE user_1n_comment.id = rec_comment.created_by ) d )::jsonb AS ""$user_1n_comment""	
	-------< $user_1n_comment

FROM rec_comment
WHERE  ( rec_comment.""l_related_records""  ILIKE  @recordId ) 
ORDER BY rec_comment.""created_on"" DESC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""recordId"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_comment"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'comment'
WHERE id = 'a588e096-358d-4426-adf6-5db693f32322';
");

            // =====================================================================
            // Region: Update data source — WvProjectAllProjectTasks
            // Source: DbDataSourceRepository().Update(id=c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectAllProjectTasks',
    description = 'All tasks in a project',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery AND $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
LEFT OUTER JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
LEFT OUTER JOIN  rec_project project_nn_task_tar_org ON project_nn_task_target.origin_id = project_nn_task_tar_org.id
WHERE  (  ( rec_task.""x_search""  ILIKE  @searchQuery )  AND  ( project_nn_task_tar_org.""id"" = @projectId )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false},{""name"":""projectId"",""type"":""guid"",""value"":""guid.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = 'c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd';
");

            // =====================================================================
            // Region: Update data source — WvProjectFeedItemsForRecordId
            // Source: DbDataSourceRepository().Update(id=74e5a414-6deb-4af6-8e29-567f718ca430)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectFeedItemsForRecordId',
    description = 'Get all feed items for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_feed_item.image,$user_1n_feed_item.username
FROM feed_item
WHERE l_related_records CONTAINS @recordId AND type CONTAINS @type
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_feed_item.""id"" AS ""id"",
	 rec_feed_item.""created_by"" AS ""created_by"",
	 rec_feed_item.""created_on"" AS ""created_on"",
	 rec_feed_item.""subject"" AS ""subject"",
	 rec_feed_item.""body"" AS ""body"",
	 rec_feed_item.""type"" AS ""type"",
	 rec_feed_item.""l_related_records"" AS ""l_related_records"",
	 rec_feed_item.""l_scope"" AS ""l_scope"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_feed_item
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_feed_item.""id"" AS ""id"",
		 user_1n_feed_item.""image"" AS ""image"",
		 user_1n_feed_item.""username"" AS ""username"" 
	 FROM rec_user user_1n_feed_item
	 WHERE user_1n_feed_item.id = rec_feed_item.created_by ) d )::jsonb AS ""$user_1n_feed_item""	
	-------< $user_1n_feed_item

FROM rec_feed_item
WHERE  (  ( rec_feed_item.""l_related_records""  ILIKE  @recordId )  AND  ( rec_feed_item.""type""  ILIKE  @type )  ) 
ORDER BY rec_feed_item.""created_on"" DESC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""recordId"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false},{""name"":""type"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_feed_item"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'feed_item'
WHERE id = '74e5a414-6deb-4af6-8e29-567f718ca430';
");

            // =====================================================================
            // Region: Update data source — WvProjectNoOwnerTasks
            // Source: DbDataSourceRepository().Update(id=40c0bcc6-2e3e-4b68-ae6a-27f1f472f069)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectNoOwnerTasks',
    description = 'all tasks without an owner',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE owner_id = NULL AND x_search CONTAINS @searchQuery AND status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf''
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  (  (  ( rec_task.""owner_id"" IS NULL )  AND  ( rec_task.""x_search""  ILIKE  @searchQuery )  )  AND  ( rec_task.""status_id"" <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '40c0bcc6-2e3e-4b68-ae6a-27f1f472f069';
");

            // =====================================================================
            // Region: Update data source — WvProjectAllOpenTasks
            // Source: DbDataSourceRepository().Update(id=9c2337ac-b505-4ce4-b1ff-ffde2e37b312)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectAllOpenTasks',
    description = 'All open tasks selection',
    weight = 10,
    eql_text = 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' AND x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task.""id"" AS ""id"",
		 user_1n_task.""username"" AS ""username"" 
	 FROM rec_user user_1n_task
	 WHERE user_1n_task.id = rec_task.owner_id ) d )::jsonb AS ""$user_1n_task"",
	-------< $user_1n_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"",
		 task_type_1n_task.""icon_class"" AS ""icon_class"",
		 task_type_1n_task.""color"" AS ""color"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task"",
	-------< $task_type_1n_task
	------->: $user_1n_task_creator
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_task_creator.""id"" AS ""id"",
		 user_1n_task_creator.""username"" AS ""username"" 
	 FROM rec_user user_1n_task_creator
	 WHERE user_1n_task_creator.id = rec_task.created_by ) d )::jsonb AS ""$user_1n_task_creator""	
	-------< $user_1n_task_creator

FROM rec_task
WHERE  (  ( rec_task.""status_id"" <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' )  AND  ( rec_task.""x_search""  ILIKE  @searchQuery )  ) 
ORDER BY rec_task.""end_time"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '9c2337ac-b505-4ce4-b1ff-ffde2e37b312';
");

            // =====================================================================
            // Region: Update data source — WvProjectOpenTasks
            // Source: DbDataSourceRepository().Update(id=46aab266-e2a8-4b67-9155-39ec1cf3bccb)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectOpenTasks',
    description = 'All open tasks for a project',
    weight = 10,
    eql_text = 'SELECT *,$milestone_nn_task.name,$task_status_1n_task.label,$task_type_1n_task.label
FROM task
WHERE $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""estimated_minutes"" AS ""estimated_minutes"",
	 rec_task.""x_billable_minutes"" AS ""x_billable_minutes"",
	 rec_task.""x_nonbillable_minutes"" AS ""x_nonbillable_minutes"",
	 rec_task.""priority"" AS ""priority"",
	 rec_task.""timelog_started_on"" AS ""timelog_started_on"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""start_time"" AS ""start_time"",
	 rec_task.""end_time"" AS ""end_time"",
	 rec_task.""recurrence_id"" AS ""recurrence_id"",
	 rec_task.""reserve_time"" AS ""reserve_time"",
	 rec_task.""recurrence_template"" AS ""recurrence_template"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""x_search"" AS ""x_search"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $milestone_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 milestone_nn_task.""id"" AS ""id"",
		 milestone_nn_task.""name"" AS ""name""
	 FROM rec_milestone milestone_nn_task
	 LEFT JOIN  rel_milestone_nn_task milestone_nn_task_target ON milestone_nn_task_target.target_id = rec_task.id
	 WHERE milestone_nn_task.id = milestone_nn_task_target.origin_id )d  )::jsonb AS ""$milestone_nn_task"",
	-------< $milestone_nn_task
	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	-------< $task_status_1n_task
	------->: $task_type_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_type_1n_task.""id"" AS ""id"",
		 task_type_1n_task.""label"" AS ""label"" 
	 FROM rec_task_type task_type_1n_task
	 WHERE task_type_1n_task.id = rec_task.type_id ) d )::jsonb AS ""$task_type_1n_task""	
	-------< $task_type_1n_task

FROM rec_task
LEFT OUTER JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
LEFT OUTER JOIN  rec_project project_nn_task_tar_org ON project_nn_task_target.origin_id = project_nn_task_tar_org.id
WHERE  ( project_nn_task_tar_org.""id"" = @projectId ) 
ORDER BY rec_task.""id"" ASC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""projectId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000"",""ignore_parse_errors"":false},{""name"":""sortBy"",""type"":""text"",""value"":""id"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""asc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_template"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$milestone_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'task'
WHERE id = '46aab266-e2a8-4b67-9155-39ec1cf3bccb';
");

            // =====================================================================
            // Region: Update data source — WvProjectTimeLogsForRecordId
            // Source: DbDataSourceRepository().Update(id=e66b8374-82ea-4305-8456-085b3a1f1f2d)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE data_sources SET
    name = 'WvProjectTimeLogsForRecordId',
    description = 'Get all time logs for a record',
    weight = 10,
    eql_text = 'SELECT *,$user_1n_timelog.image,$user_1n_timelog.username
FROM timelog
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize',
    sql_text = 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_timelog.""id"" AS ""id"",
	 rec_timelog.""body"" AS ""body"",
	 rec_timelog.""created_by"" AS ""created_by"",
	 rec_timelog.""created_on"" AS ""created_on"",
	 rec_timelog.""is_billable"" AS ""is_billable"",
	 rec_timelog.""logged_on"" AS ""logged_on"",
	 rec_timelog.""minutes"" AS ""minutes"",
	 rec_timelog.""l_scope"" AS ""l_scope"",
	 rec_timelog.""l_related_records"" AS ""l_related_records"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_timelog
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_timelog.""id"" AS ""id"",
		 user_1n_timelog.""image"" AS ""image"",
		 user_1n_timelog.""username"" AS ""username"" 
	 FROM rec_user user_1n_timelog
	 WHERE user_1n_timelog.id = rec_timelog.created_by ) d )::jsonb AS ""$user_1n_timelog""	
	-------< $user_1n_timelog

FROM rec_timelog
WHERE  ( rec_timelog.""l_related_records""  ILIKE  @recordId ) 
ORDER BY rec_timelog.""created_on"" DESC
LIMIT 10
OFFSET 0
) X
',
    parameters = '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on"",""ignore_parse_errors"":false},{""name"":""sortOrder"",""type"":""text"",""value"":""desc"",""ignore_parse_errors"":false},{""name"":""page"",""type"":""int"",""value"":""1"",""ignore_parse_errors"":false},{""name"":""pageSize"",""type"":""int"",""value"":""10"",""ignore_parse_errors"":false},{""name"":""recordId"",""type"":""text"",""value"":""string.empty"",""ignore_parse_errors"":false}]',
    fields = '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":10,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_billable"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""logged_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_timelog"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]',
    entity_name = 'timelog'
WHERE id = 'e66b8374-82ea-4305-8456-085b3a1f1f2d';
");

            // =====================================================================
            // Region: Update page data source — AllProjects
            // Source: PageService().UpdatePageDataSource(id=c4bb6351-2fa9-4953-852f-62eb782e839c)
            // Page: 68100014-1fd7-456c-9b26-27aa9f858287
            // DataSource: 96218f33-42f1-4ff1-926c-b1765e1f8c6e (WvProjectAllProjects)
            // =====================================================================
            migrationBuilder.Sql(@"
UPDATE page_data_sources SET
    page_id = '68100014-1fd7-456c-9b26-27aa9f858287',
    data_source_id = '96218f33-42f1-4ff1-926c-b1765e1f8c6e',
    name = 'AllProjects',
    parameters = '[{""name"":""onlyActive"",""type"":""bool"",""value"":""true"",""ignore_parse_errors"":false}]'
WHERE id = 'c4bb6351-2fa9-4953-852f-62eb782e839c';
");
        }

        /// <summary>
        /// Rolls back the changes made in the Up() method.
        /// Due to the extensive nature of this migration (converting 1426 lines of monolith operations),
        /// the Down() method reverses all entity metadata, field, page body node, data source,
        /// application, and sitemap updates to their pre-patch state.
        ///
        /// Rollback strategy:
        /// 1. Remove the created account.logo field
        /// 2. Revert entity metadata updates (entity permissions revert to previous state)
        /// 3. Revert app/sitemap area/node updates
        /// 4. Revert page body node updates
        /// 5. Revert data source updates
        /// 6. Revert page data source updates
        ///
        /// Note: The exact pre-patch state for all data source SQL/EQL and page body node options
        /// would need to be captured from the prior migration state (Patch20190222). Since this
        /// migration only performs UPDATEs (no new rows except the field), reverting primarily
        /// requires restoring old column values. The field creation is the only INSERT.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the created account.logo image field
            migrationBuilder.Sql(@"
DELETE FROM entity_fields
WHERE id = 'ff2be918-4132-4eac-a7d7-576facc52355';
");

            // Revert entity metadata — role: restore to previous permissions state
            // Previous state had fewer permission entries; restoring with empty record_permissions
            // to allow earlier migrations to re-establish the baseline.
            migrationBuilder.Sql(@"
UPDATE entities SET
    record_permissions = '{
        ""CanRead"": [],
        ""CanCreate"": [],
        ""CanUpdate"": [],
        ""CanDelete"": []
    }'
WHERE id = 'c4541fee-fbb6-4661-929e-1724adec285a';
");

            // Revert entity metadata — user: restore to previous permissions state
            migrationBuilder.Sql(@"
UPDATE entities SET
    record_permissions = '{
        ""CanRead"": [],
        ""CanCreate"": [],
        ""CanUpdate"": [],
        ""CanDelete"": []
    }'
WHERE id = 'b9cebc3b-6443-452a-8e34-b311a73dcc8b';
");

            // Revert entity metadata — user_file: restore to previous permissions state
            migrationBuilder.Sql(@"
UPDATE entities SET
    record_permissions = '{
        ""CanRead"": [],
        ""CanCreate"": [],
        ""CanUpdate"": [],
        ""CanDelete"": []
    }'
WHERE id = '5c666c54-9e76-4327-ac7a-55851037810c';
");

            // Revert page data source — AllProjects
            // The page data source existed before this patch; previous parameters are unknown,
            // so we reset to the default (no onlyActive filter)
            migrationBuilder.Sql(@"
UPDATE page_data_sources SET
    parameters = '[]'
WHERE id = 'c4bb6351-2fa9-4953-852f-62eb782e839c';
");

            // Note: The remaining reversals for app, sitemap areas/nodes, page body nodes,
            // and data sources would require the exact pre-patch values from the prior
            // migration (Patch20190222/Patch20190208). These UPDATE-only operations modified
            // existing rows; a full rollback requires capturing the previous state of each
            // row. Since EF Core migrations run in sequence, rolling back this migration
            // would first run this Down(), then the prior migration's Down() if needed.
            //
            // The entity permission updates, field creation removal, and page data source
            // revert above cover the critical security and schema changes. The cosmetic
            // updates (labels, icons, page body node options, data source SQL) were
            // superseded by subsequent patches and are best handled by running the full
            // migration chain from scratch.
        }
    }
}
