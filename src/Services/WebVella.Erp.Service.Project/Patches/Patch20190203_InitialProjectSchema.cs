using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebVella.Erp.Service.Project.Patches
{
    /// <summary>
    /// Initial Project schema migration - converts monolith ProjectPlugin.Patch20190203 (11,035 lines)
    /// into an EF Core migration. Creates the "projects" application, sitemap structure,
    /// all pages, all page body nodes, and all data sources.
    /// 
    /// Operations: 1 application, 7 sitemap areas, 7 sitemap nodes,
    /// 20 pages, 242 page body nodes, 19 data sources, 42 page data sources
    /// </summary>
    [Migration("20190203000000")]
    public class Patch20190203_InitialProjectSchema : Migration
    {
        /// <summary>
        /// Applies the initial project schema migration.
        /// Creates the entire "projects" application metadata including sitemap,
        /// pages, page body nodes (UI components), data sources, and page data source bindings.
        /// All GUIDs are deterministic and match the original monolith patch exactly.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ========================================
            // Application Creation
            // ========================================

            // << ***Create app*** App name: projects >>
            migrationBuilder.Sql(@"INSERT INTO applications (id, name, label, description, icon_class, author, color, weight, access) VALUES ('652ccabf-d5ad-46d8-aa67-25842537ed4c', 'projects', 'Projects', 'Project management, task and time accounting', 'fa fa-code', 'WebVella', '#9c27b0', 1, '[""bdc56420-caf0-4030-8a0e-d264938e0cda""]');");


            // ========================================
            // Sitemap Areas
            // ========================================

            // << ***Create sitemap area*** Sitemap area name: dashboard >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('d99e07df-b5f3-4a01-8506-b607c3389308', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'dashboard', 'Dashboard', '[]', '', '[]', 'fas fa-tachometer-alt', '#9C27B0', 1, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: track-time >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('fe9ac91f-a52f-4127-a74b-c4b335930c1d', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'track-time', 'Track Time', '[]', 'User time track', '[]', 'fas fa-clock', '#9C27B0', 2, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: feed >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('24028a64-748b-43a2-98ae-47514da142fe', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'feed', 'Feed', '[]', '', '[]', 'fas fa-rss-square', '#9C27B0', 3, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: tasks >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('9aacb1b4-c03d-44bb-8d79-554971f4a25c', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'tasks', 'Tasks', '[]', '', '[]', 'fas fa-check-double', '#9C27B0', 4, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: projects >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('dadd2bb1-459b-48da-a798-f2eea579c4e5', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'projects', 'Projects', '[]', '', '[]', 'fa fa-cogs', '#9C27B0', 5, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: accounts >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('b7ddb30a-0d8b-4d52-a392-5cc6136fb7a4', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'accounts', 'Accounts', '[]', 'list of all accounts in the system', '[]', 'fas fa-user-tie', '#9C27B0', 6, false, '[]');");

            // << ***Create sitemap area*** Sitemap area name: reports >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_areas (id, app_id, name, label, label_translations, description, description_translations, icon_class, color, weight, show_group_names, access) VALUES ('83ebdcfd-a244-4fba-9e25-f96fe27b7d0d', '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'reports', 'Reports', '[]', '', '[]', 'fa fa-database', '#9C27B0', 7, false, '[]');");


            // ========================================
            // Sitemap Nodes
            // ========================================

            // << ***Create sitemap node*** Sitemap node name: dashboard >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('3edb7097-a998-4e2e-9ba0-716f0767ce35', 'd99e07df-b5f3-4a01-8506-b607c3389308', 'dashboard', 'Dashboard', '[]', 'fas fa-tachometer-alt', '/projects/dashboard/dashboard/a', 2, NULL, 1, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: track-time >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('8c27983c-d215-48ad-9e73-49fd4e8acdb8', 'fe9ac91f-a52f-4127-a74b-c4b335930c1d', 'track-time', 'Track Time', '[]', 'fas fa-clock', '', 2, NULL, 1, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: feed >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('8950c6c6-7848-4a0b-b260-e8dbedf7486c', '24028a64-748b-43a2-98ae-47514da142fe', 'feed', 'Feed', '[]', 'fas fa-rss-square', '/projects/feed/feed/a', 2, NULL, 1, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: tasks >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('dda5c020-c2bd-4f1f-9d8d-447659decc15', '9aacb1b4-c03d-44bb-8d79-554971f4a25c', 'tasks', 'All tasks', '[]', 'fas fa-list-ul', '', 1, '9386226e-381e-4522-b27b-fb5514d77902', 2, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: projects >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('48200d8b-6b7d-47b5-931c-17033ad8a679', 'dadd2bb1-459b-48da-a798-f2eea579c4e5', 'projects', 'All projects', '[]', 'fas fa-list-ul', '', 1, '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', 2, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: accounts >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('98c2a9bc-5576-4b90-b72c-d2cd7a3da5a5', 'b7ddb30a-0d8b-4d52-a392-5cc6136fb7a4', 'accounts', 'Accounts', '[]', 'fas fa-user-tie', '', 1, '2e22b50f-e444-4b62-a171-076e51246939', 1, '[]', '[]', '[]', '[]', '[]');");

            // << ***Create sitemap node*** Sitemap node name: list >>
            migrationBuilder.Sql(@"INSERT INTO app_sitemap_nodes (id, area_id, name, label, label_translations, icon_class, url, type, entity_id, weight, access, entity_list_pages, entity_create_pages, entity_details_pages, entity_manage_pages) VALUES ('f04a7e50-f56a-4c5a-aa82-8b1028a05eeb', '83ebdcfd-a244-4fba-9e25-f96fe27b7d0d', 'list', 'Report List', '[]', 'fas fa-list-ul', '', 2, NULL, 1, '[]', '[]', '[]', '[]', '[]');");


            // ========================================
            // Pages
            // ========================================

            // << ***Create page*** Page name: list >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('84b892fc-6ca4-4c7e-8b7c-2f2f6954862f', 'list', 'Reports List', '[]', NULL, false, 1, 2, '652ccabf-d5ad-46d8-aa67-25842537ed4c', NULL, 'f04a7e50-f56a-4c5a-aa82-8b1028a05eeb', '83ebdcfd-a244-4fba-9e25-f96fe27b7d0d', false, NULL, '');");

            // << ***Create page*** Page name: open >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('273dd749-3804-48c8-8306-078f1e7f3b3f', 'open', 'Open tasks', '[]', NULL, false, 5, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: all >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('57db749f-e69e-4d88-b9d1-66203da05da1', 'all', 'All Projects', '[]', NULL, false, 10, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: create >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('c2e38698-24cd-4209-b560-02c225f3ff4a', 'create', 'Create Project', '[]', '', false, 10, 4, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: dashboard >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('33f2cd33-cf38-4247-9097-75f895d1ef7a', 'dashboard', 'My Dashboard', '[]', NULL, false, 10, 2, '652ccabf-d5ad-46d8-aa67-25842537ed4c', NULL, '3edb7097-a998-4e2e-9ba0-716f0767ce35', 'd99e07df-b5f3-4a01-8506-b607c3389308', false, NULL, '');");

            // << ***Create page*** Page name: dashboard >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('50e4e84d-4148-4635-8372-4f2262747668', 'dashboard', 'Project dashboard', '[]', NULL, false, 10, 5, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: details >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('7a0aad34-0f2f-4c40-a77f-cee92c9550a3', 'details', 'Project details', '[]', '', false, 10, 5, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: milestones >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('d07cbf70-09c6-47ee-9a13-80568e43d331', 'milestones', 'Project milestones', '[]', '', false, 10, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', 'c15f030a-9d94-4767-89aa-c55a09f8b83e', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: create >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('68100014-1fd7-456c-9b26-27aa9f858287', 'create', 'Create task', '[]', NULL, false, 10, 4, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: details >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', 'details', 'Task details', '[]', '', false, 10, 5, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: no-owner >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', 'no-owner', 'Open tasks without owner', '[]', NULL, false, 10, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: list >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('2f11031a-41da-4dfc-8e40-ddc6dca71e2c', 'list', 'Accounts List', '[]', NULL, false, 10, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2e22b50f-e444-4b62-a171-076e51246939', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: details >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('80b10445-c850-44cf-9c8c-57daca671dcf', 'details', 'Account details', '[]', NULL, false, 10, 5, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2e22b50f-e444-4b62-a171-076e51246939', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: create >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('d4b31a98-b1ed-44b5-aa69-32a6fc87205e', 'create', 'Create Account', '[]', NULL, false, 10, 4, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2e22b50f-e444-4b62-a171-076e51246939', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: feed >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('acb76466-32b8-428c-81cb-47b6013879e7', 'feed', 'My Watch Feed', '[]', NULL, false, 10, 2, '652ccabf-d5ad-46d8-aa67-25842537ed4c', NULL, '8950c6c6-7848-4a0b-b260-e8dbedf7486c', '24028a64-748b-43a2-98ae-47514da142fe', false, NULL, '');");

            // << ***Create page*** Page name: track-time >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('e9c8f7ef-4714-40e9-90cd-3814d89603b1', 'track-time', 'Track Time', '[]', NULL, false, 10, 2, '652ccabf-d5ad-46d8-aa67-25842537ed4c', NULL, '8c27983c-d215-48ad-9e73-49fd4e8acdb8', 'fe9ac91f-a52f-4127-a74b-c4b335930c1d', false, NULL, '');");

            // << ***Create page*** Page name: all >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('6d3fe557-59dd-4a2e-b710-f3f326ae172b', 'all', 'All tasks', '[]', NULL, false, 20, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: feed >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('dfe56667-174d-492d-8f84-b8ab8b70c63f', 'feed', 'Project feed', '[]', NULL, false, 100, 5, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '2d9b2d1d-e32b-45e1-a013-91d92a9ce792', NULL, NULL, false, NULL, '');");

            // << ***Create page*** Page name: account-monthly-timelog >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('d23be591-dbb5-4795-86e4-8adbd9aff08b', 'account-monthly-timelog', 'Report: Monthly Timelog for an account', '[]', NULL, false, 1000, 2, '652ccabf-d5ad-46d8-aa67-25842537ed4c', NULL, 'f04a7e50-f56a-4c5a-aa82-8b1028a05eeb', '83ebdcfd-a244-4fba-9e25-f96fe27b7d0d', false, NULL, '');");

            // << ***Create page*** Page name: tasks >>
            migrationBuilder.Sql(@"INSERT INTO pages (id, name, label, label_translations, icon_class, system, weight, type, app_id, entity_id, node_id, area_id, is_razor_body, razor_body, layout) VALUES ('6f673561-fad7-4844-8262-589834f1b2ce', 'tasks', 'Project tasks', '[]', NULL, false, 1000, 3, '652ccabf-d5ad-46d8-aa67-25842537ed4c', '9386226e-381e-4522-b27b-fb5514d77902', NULL, NULL, false, NULL, '');");


            // ========================================
            // Page Body Nodes (UI Components)
            // ========================================

            // << ***Create page body node*** Page name: track-time  id: 6bb17b95-258a-4572-99f3-898d1895cfba >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6bb17b95-258a-4572-99f3-898d1895cfba', NULL, 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 2, 'WebVella.Erp.Web.Components.PcValidation', '', '{
  ""validation"": ""{\""type\"":\""0\"",\""string\"":\""Validation\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: tasks  id: f4f2b086-1181-4db5-b78f-51d1b41e1611 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f4f2b086-1181-4db5-b78f-51d1b41e1611', NULL, '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 3, 'WebVella.Erp.Web.Components.PcDrawer', '', '{
  ""title"": ""Search Tasks"",
  ""width"": ""550px"",
  ""class"": """",
  ""body_class"": """",
  ""title_action_html"": """"
}');");

            // << ***Create page body node*** Page name: tasks  id: 7590ab09-b749-4051-935a-b51d16d7b76a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7590ab09-b749-4051-935a-b51d16d7b76a', 'f4f2b086-1181-4db5-b78f-51d1b41e1611', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-7590ab09-b749-4051-935a-b51d16d7b76a"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: tasks  id: df667b11-30ac-4b6b-a12d-41e5aaf6cae5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('df667b11-30ac-4b6b-a12d-41e5aaf6cae5', '7590ab09-b749-4051-935a-b51d16d7b76a', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""1"",
  ""text"": ""Search Tasks"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: tasks  id: 57789d88-e897-4b7b-9999-239821db4274 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('57789d88-e897-4b7b-9999-239821db4274', '7590ab09-b749-4051-935a-b51d16d7b76a', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcGridFilterField', 'body', '{
  ""label"": ""Task contents"",
  ""name"": ""x_search"",
  ""try_connect_to_entity"": ""true"",
  ""field_type"": ""18"",
  ""query_type"": ""2"",
  ""query_options"": [
    ""2""
  ],
  ""prefix"": """"
}');");

            // << ***Create page body node*** Page name: list  id: dedd97f6-1b09-4942-aae1-684cdc49a3eb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('dedd97f6-1b09-4942-aae1-684cdc49a3eb', NULL, '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 2,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""Accounts\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No accounts matching your query"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""20px"",
  ""container1_name"": ""action"",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_searchable"": ""false"",
  ""container2_label"": ""name"",
  ""container2_width"": """",
  ""container2_name"": ""name"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_searchable"": ""false"",
  ""container3_label"": ""column3"",
  ""container3_width"": """",
  ""container3_name"": ""column3"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_searchable"": ""false"",
  ""container4_label"": ""column4"",
  ""container4_width"": """",
  ""container4_name"": ""column4"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_searchable"": ""false"",
  ""container5_label"": ""column5"",
  ""container5_width"": """",
  ""container5_name"": ""column5"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_searchable"": ""false"",
  ""container6_label"": ""column6"",
  ""container6_width"": """",
  ""container6_name"": ""column6"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_searchable"": ""false"",
  ""container7_label"": ""column7"",
  ""container7_width"": """",
  ""container7_name"": ""column7"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_searchable"": ""false"",
  ""container8_label"": ""column8"",
  ""container8_width"": """",
  ""container8_name"": ""column8"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_searchable"": ""false"",
  ""container9_label"": ""column9"",
  ""container9_width"": """",
  ""container9_name"": ""column9"",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_searchable"": ""false"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_searchable"": ""false"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_searchable"": ""false"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_searchable"": ""false""
}');");

            // << ***Create page body node*** Page name: list  id: 8f61ba2d-9c8a-434d-9f78-d12926cd80ef >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8f61ba2d-9c8a-434d-9f78-d12926cd80ef', 'dedd97f6-1b09-4942-aae1-684cdc49a3eb', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.name\"",\""default\"":\""Account Name\""}"",
  ""name"": ""name"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: list  id: c0689f85-235d-484e-bea3-e534e6e10094 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c0689f85-235d-484e-bea3-e534e6e10094', 'dedd97f6-1b09-4942-aae1-684cdc49a3eb', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//replace constants with your values\\n\\t\\tconst string DATASOURCE_NAME = \\\""RowRecord.id\\\"";\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<Guid>(DATASOURCE_NAME);\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (dataSource == null)\\n\\t\\t\\treturn null;\\n\\n        \\n\\t\\treturn $\\\""/projects/accounts/accounts/r/{dataSource}\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: list  id: 13a1d868-93ee-41d1-bb94-231d99899f74 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('13a1d868-93ee-41d1-bb94-231d99899f74', NULL, '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": """",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: list  id: 77abedcf-4bea-46f3-b50c-340a7aa237d6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('77abedcf-4bea-46f3-b50c-340a7aa237d6', '13a1d868-93ee-41d1-bb94-231d99899f74', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""2"",
  ""text"": ""New Account"",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-plus go-green"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""/projects/accounts/accounts/c"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: list  id: b9258d04-360b-426f-b542-ec458f946edf >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b9258d04-360b-426f-b542-ec458f946edf', '13a1d868-93ee-41d1-bb94-231d99899f74', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""0"",
  ""text"": ""Search"",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-search"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": ""ErpEvent.DISPATCH(\""WebVella.Erp.Web.Components.PcDrawer\"",\""open\"")"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: feed  id: e1e493ac-6b74-490f-a0e3-ffd2f2f71f1b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e1e493ac-6b74-490f-a0e3-ffd2f2f71f1b', NULL, 'acb76466-32b8-428c-81cb-47b6013879e7', NULL, 2, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""App.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fas fa-rss-square"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: dashboard  id: a584a5ed-96a2-4a28-95e8-23266bc36926 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a584a5ed-96a2-4a28-95e8-23266bc36926', NULL, '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 2,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 12,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 6,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 12,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 6,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: dashboard  id: 63daa5c0-ed7f-432e-bfbb-746b94207146 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('63daa5c0-ed7f-432e-bfbb-746b94207146', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""My Overdue Tasks"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3 "",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: d4c6bc3b-51d5-4f2d-a329-f02c59250a41 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d4c6bc3b-51d5-4f2d-a329-f02c59250a41', '63daa5c0-ed7f-432e-bfbb-746b94207146', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 2, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksQueue', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}"",
  ""type"": ""1""
}');");

            // << ***Create page body node*** Page name: dashboard  id: ae930e6f-38b5-4c48-a17f-63b0bdf7dab6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ae930e6f-38b5-4c48-a17f-63b0bdf7dab6', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 3, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""All Users'' Timesheet"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 483e09f0-98c4-4e70-ad9a-3a92abebaf74 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('483e09f0-98c4-4e70-ad9a-3a92abebaf74', 'ae930e6f-38b5-4c48-a17f-63b0bdf7dab6', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTimesheet', 'body', '""{}""');");

            // << ***Create page body node*** Page name: dashboard  id: 151e265c-d3d3-4340-92fc-0cace2ca45f9 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('151e265c-d3d3-4340-92fc-0cace2ca45f9', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'column1', '{
  ""visible_columns"": 2,
  ""class"": ""mb-3"",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: dashboard  id: 47303562-04a3-4935-b228-aaa61527f963 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('47303562-04a3-4935-b228-aaa61527f963', '151e265c-d3d3-4340-92fc-0cace2ca45f9', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""Tasks"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 5b95ff72-dfc0-4a99-ad3a-6c6107f7bd4c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5b95ff72-dfc0-4a99-ad3a-6c6107f7bd4c', '47303562-04a3-4935-b228-aaa61527f963', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksChart', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: be907fa3-0971-45b5-9dcf-fabbb277fe54 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('be907fa3-0971-45b5-9dcf-fabbb277fe54', '151e265c-d3d3-4340-92fc-0cace2ca45f9', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Priority"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 209d32c9-6c2f-4f45-859a-3ae2718ebf88 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('209d32c9-6c2f-4f45-859a-3ae2718ebf88', 'be907fa3-0971-45b5-9dcf-fabbb277fe54', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksPriorityChart', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 8e533c53-0bf5-4082-ae06-f47f1bd9b3b5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8e533c53-0bf5-4082-ae06-f47f1bd9b3b5', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 3, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""My 10 Upcoming Tasks "",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: f35b6e4b-3c81-409c-8f01-18e0d457e9ff >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f35b6e4b-3c81-409c-8f01-18e0d457e9ff', '8e533c53-0bf5-4082-ae06-f47f1bd9b3b5', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksQueue', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}"",
  ""type"": ""3""
}');");

            // << ***Create page body node*** Page name: dashboard  id: e49cf2f9-82b0-4988-aa29-427e8d9501d9 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e49cf2f9-82b0-4988-aa29-427e8d9501d9', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 2, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""My Timesheet"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: c510d93d-e3d5-40d2-9655-73a3d2f63020 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c510d93d-e3d5-40d2-9655-73a3d2f63020', 'e49cf2f9-82b0-4988-aa29-427e8d9501d9', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTimesheet', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 6ef7bbd7-b96c-45d4-97e1-b8e43f489ed5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6ef7bbd7-b96c-45d4-97e1-b8e43f489ed5', 'a584a5ed-96a2-4a28-95e8-23266bc36926', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 2, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""My Tasks Due Today"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: ec0da060-7367-4263-b3fa-7c32765c97c5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ec0da060-7367-4263-b3fa-7c32765c97c5', '6ef7bbd7-b96c-45d4-97e1-b8e43f489ed5', '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksQueue', 'body', '{
  ""project_id"": """",
  ""user_id"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}"",
  ""type"": ""2""
}');");

            // << ***Create page body node*** Page name: dashboard  id: f68e4fb5-64d1-48ff-8846-e0ec36aa7e69 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f68e4fb5-64d1-48ff-8846-e0ec36aa7e69', NULL, '33f2cd33-cf38-4247-9097-75f895d1ef7a', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""App.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fas fa-tachometer-alt"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: d3501ea7-86f2-4230-8bc5-30ffab78be5e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d3501ea7-86f2-4230-8bc5-30ffab78be5e', NULL, 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 3, 'WebVella.Erp.Plugins.Project.Components.PcReportAccountMonthlyTimelogs', '', '{
  ""year"": ""{\""type\"":\""0\"",\""string\"":\""RequestQuery.year\"",\""default\"":\""\""}"",
  ""month"": ""{\""type\"":\""0\"",\""string\"":\""RequestQuery.month\"",\""default\"":\""\""}"",
  ""account_id"": ""{\""type\"":\""0\"",\""string\"":\""RequestQuery.account\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: tasks  id: c984f52a-5121-471d-ae66-e8a64de68c3d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c984f52a-5121-471d-ae66-e8a64de68c3d', NULL, '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 8,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""AllProjectTasks\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No records"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_searchable"": ""false"",
  ""container2_label"": ""type"",
  ""container2_width"": ""20px"",
  ""container2_name"": ""type"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_searchable"": ""false"",
  ""container3_label"": ""key"",
  ""container3_width"": ""80px"",
  ""container3_name"": ""key"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_searchable"": ""false"",
  ""container4_label"": ""task"",
  ""container4_width"": """",
  ""container4_name"": ""task"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_searchable"": ""false"",
  ""container5_label"": ""owner"",
  ""container5_width"": ""120px"",
  ""container5_name"": ""owner_id"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_searchable"": ""false"",
  ""container6_label"": ""created by"",
  ""container6_width"": ""120px"",
  ""container6_name"": ""created_by"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_searchable"": ""false"",
  ""container7_label"": ""target date"",
  ""container7_width"": ""120px"",
  ""container7_name"": ""target_date"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_searchable"": ""false"",
  ""container8_label"": ""status"",
  ""container8_width"": ""80px"",
  ""container8_name"": ""status"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_searchable"": ""false"",
  ""container9_label"": ""column9"",
  ""container9_width"": """",
  ""container9_name"": ""column9"",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_searchable"": ""false"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_searchable"": ""false"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_searchable"": ""false"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_searchable"": ""false""
}');");

            // << ***Create page body node*** Page name: tasks  id: d088ba1c-15b8-48b9-8673-a871338cbdea >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d088ba1c-15b8-48b9-8673-a871338cbdea', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.subject\"",\""default\"":\""Task subject\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: tasks  id: 064ea82a-c5c2-40dd-96e4-7859aa879b14 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('064ea82a-c5c2-40dd-96e4-7859aa879b14', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column5', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: tasks  id: e83f6542-f9f8-4fec-aeb8-48731951f182 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e83f6542-f9f8-4fec-aeb8-48731951f182', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column7', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.target_date\"",\""default\"":\""n/a\""}"",
  ""name"": ""target_date"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4""
}');");

            // << ***Create page body node*** Page name: tasks  id: cfa8a277-5447-45f2-ad06-26818381b54a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('cfa8a277-5447-45f2-ad06-26818381b54a', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column3', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.key\"",\""default\"":\""key\""}"",
  ""name"": ""key"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: tasks  id: 9cd708aa-60c5-4dfa-b95c-73e5508aec64 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9cd708aa-60c5-4dfa-b95c-73e5508aec64', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column8', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$task_status_1n_task[0].label\"",\""default\"":\""n/a\""}"",
  ""name"": ""status_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: tasks  id: ad1e60ec-813c-4b1d-aa33-ad76d705d5d9 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ad1e60ec-813c-4b1d-aa33-ad76d705d5d9', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column6', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task_creator[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: tasks  id: a20664ce-a3fe-436a-84f0-42f4e14564c1 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a20664ce-a3fe-436a-84f0-42f4e14564c1', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar typeRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord.$task_type_1n_task[0]\\\"");\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (typeRecord == null)\\n\\t\\t\\treturn null;\\n\\n        var iconClass=\\\""fa fa-fw fa-file\\\"";\\n        var color=\\\""#999\\\"";\\n        if(typeRecord[\\\""icon_class\\\""] != null){\\n            iconClass = (string)typeRecord[\\\""icon_class\\\""];\\n        }\\n        if(typeRecord[\\\""color\\\""] != null){\\n            color = (string)typeRecord[\\\""color\\\""];\\n        }\\n\\t\\treturn $\\\""<i class=\\\\\\\""{iconClass}\\\\\\\"" style=\\\\\\\""color:{color};font-size:23px;\\\\\\\"" title=\\\\\\\""{typeRecord[\\\""label\\\""]}\\\\\\\""></i>\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""icon\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: tasks  id: 4f298a92-4592-4714-948e-eaebb3962785 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4f298a92-4592-4714-948e-eaebb3962785', 'c984f52a-5121-471d-ae66-e8a64de68c3d', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar taskId = pageModel.TryGetDataSourceProperty<Guid>(\\\""RowRecord.id\\\"");\\n        var projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""ParentRecord.id\\\"");\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (taskId == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\treturn $\\\""/projects/tasks/tasks/r/{taskId}/details?returnUrl=/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: d65f22f5-6644-4ca9-81ce-c3ce5898f8b5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d65f22f5-6644-4ca9-81ce-c3ce5898f8b5', NULL, 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""App.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""far fa-clock"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: feed  id: 33cba2bb-6070-4b00-ba92-64064077a49b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('33cba2bb-6070-4b00-ba92-64064077a49b', NULL, 'dfe56667-174d-492d-8f84-b8ab8b70c63f', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.abbr\"",\""default\"":\""\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-rss"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: feed  id: c50ad432-98f2-4140-a40c-3157fc52f93c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c50ad432-98f2-4140-a40c-3157fc52f93c', '33cba2bb-6070-4b00-ba92-64064077a49b', 'dfe56667-174d-492d-8f84-b8ab8b70c63f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'toolbar', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\tvar projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""Record.id\\\"");\\n        var pageName = pageModel.TryGetDataSourceProperty<string>(\\\""Page.Name\\\"");\\n\\n\\t\\tif (projectId == null || pageName == null)\\n\\t\\t\\treturn null;\\n\\n        var result = $\\\""<a href=''/projects/projects/projects/r/{projectId}/dashboard'' class=''btn btn-link btn-sm {(pageName == \\\""dashboard\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Dashboard</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/feed'' class=''btn btn-link btn-sm {(pageName == \\\""feed\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Feed</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks'' class=''btn btn-link btn-sm {(pageName == \\\""tasks\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Tasks</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/55c8d6e2-f26d-4689-9d1b-a8c1b9de1672/l/milestones'' class=''btn btn-link btn-sm {(pageName == \\\""milestones\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Milestones</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/details'' class=''btn btn-link btn-sm {(pageName == \\\""details\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Details</a>\\\"";\\n\\t\\treturn result;\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: track-time  id: e84c527a-4feb-4d60-ab91-4b1ecd89b39c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e84c527a-4feb-4d60-ab91-4b1ecd89b39c', NULL, 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 4, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 4,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""TrackTimeTasks\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""false"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No records"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""false"",
  ""container1_label"": ""my tasks "",
  ""container1_width"": """",
  ""container1_name"": ""task"",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_class"": """",
  ""container1_vertical_align"": ""3"",
  ""container1_horizontal_align"": ""1"",
  ""container2_label"": ""logged"",
  ""container2_width"": ""120px"",
  ""container2_name"": ""logged"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_class"": ""timer-td"",
  ""container2_vertical_align"": ""3"",
  ""container2_horizontal_align"": ""1"",
  ""container3_label"": ""timelog"",
  ""container3_width"": ""150px"",
  ""container3_name"": """",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_class"": """",
  ""container3_vertical_align"": ""3"",
  ""container3_horizontal_align"": ""1"",
  ""container4_label"": ""action"",
  ""container4_width"": ""100px"",
  ""container4_name"": """",
  ""container4_nowrap"": ""true"",
  ""container4_sortable"": ""false"",
  ""container4_class"": """",
  ""container4_vertical_align"": ""1"",
  ""container4_horizontal_align"": ""1"",
  ""container5_label"": ""column5"",
  ""container5_width"": """",
  ""container5_name"": ""column5"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_class"": """",
  ""container5_vertical_align"": ""1"",
  ""container5_horizontal_align"": ""1"",
  ""container6_label"": ""column6"",
  ""container6_width"": """",
  ""container6_name"": ""column6"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_class"": """",
  ""container6_vertical_align"": ""1"",
  ""container6_horizontal_align"": ""1"",
  ""container7_label"": ""column7"",
  ""container7_width"": """",
  ""container7_name"": ""column7"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_class"": """",
  ""container7_vertical_align"": ""1"",
  ""container7_horizontal_align"": ""1"",
  ""container8_label"": ""column8"",
  ""container8_width"": """",
  ""container8_name"": ""column8"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_class"": """",
  ""container8_vertical_align"": ""1"",
  ""container8_horizontal_align"": ""1"",
  ""container9_label"": ""column9"",
  ""container9_width"": """",
  ""container9_name"": ""column9"",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_class"": """",
  ""container9_vertical_align"": ""1"",
  ""container9_horizontal_align"": ""1"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_class"": """",
  ""container10_vertical_align"": ""1"",
  ""container10_horizontal_align"": ""1"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_class"": """",
  ""container11_vertical_align"": ""1"",
  ""container11_horizontal_align"": ""1"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_class"": """",
  ""container12_vertical_align"": ""1"",
  ""container12_horizontal_align"": ""1""
}');");

            // << ***Create page body node*** Page name: track-time  id: 3d0eb8a7-1182-4974-a039-433954aa8d7c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3d0eb8a7-1182-4974-a039-433954aa8d7c', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldHidden', 'column3', '{
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.id\"",\""default\"":\""\""}"",
  ""name"": ""task_id"",
  ""try_connect_to_entity"": ""false""
}');");

            // << ***Create page body node*** Page name: track-time  id: 46657d5a-0102-43b7-9ca3-9259953d37b6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('46657d5a-0102-43b7-9ca3-9259953d37b6', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 2, 'WebVella.Erp.Web.Components.PcBtnGroup', 'column3', '{
  ""size"": ""3"",
  ""is_vertical"": ""false"",
  ""class"": ""d-none stop-log-group w-100"",
  ""id"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 700954cc-7407-4b20-81de-a882380e5d4d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('700954cc-7407-4b20-81de-a882380e5d4d', '46657d5a-0102-43b7-9ca3-9259953d37b6', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""0"",
  ""text"": ""stop logging"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": ""btn-block stop-log"",
  ""id"": """",
  ""icon_class"": ""fas fa-fw fa-square go-red"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: c57d94a6-9c90-4071-b54b-2c05b79aa522 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c57d94a6-9c90-4071-b54b-2c05b79aa522', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord\\\"");\\n\\t\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (dataSource == null)\\n\\t\\t\\t\\treturn null;\\n\\t        var loggedSeconds = ((int)dataSource[\\\""logged_minutes\\\""])*60;\\n\\t        var logStartedOn = (DateTime?)dataSource[\\\""timelog_started_on\\\""];\\n\\t        var logStartString = \\\""\\\"";\\n\\t        if(logStartedOn != null){\\n\\t            loggedSeconds = loggedSeconds + (int)((DateTime.UtcNow - logStartedOn.Value).TotalSeconds);\\n\\t            logStartString = logStartedOn.Value.ToString(\\\""o\\\"");\\n\\t        }\\n\\n\\t        var hours = (int)(loggedSeconds/3600);\\n\\t        var loggedSecondsLeft = loggedSeconds - hours*3600;\\n\\t        var hoursString = \\\""00\\\"";\\n\\t        if(hours < 10)\\n\\t            hoursString = \\\""0\\\"" + hours;\\n            else\\n                hoursString = hours.ToString();\\n\\t            \\n\\t        var minutes = (int)(loggedSecondsLeft/60);\\n\\t        var minutesString = \\\""00\\\"";\\n\\t        if(minutes < 10)\\n\\t            minutesString = \\\""0\\\"" + minutes;\\n            else\\n                minutesString = minutes.ToString();\\t        \\n                \\n            var seconds =  loggedSecondsLeft -  minutes*60;\\n\\t        var secondsString = \\\""00\\\"";\\n\\t        if(seconds < 10)\\n\\t            secondsString = \\\""0\\\"" + seconds;\\n            else\\n                secondsString = seconds.ToString();\\t                    \\n            \\n            var result = $\\\""<span class=''go-gray wv-timer'' style=''font-size:16px;''>{hoursString + \\\"" : \\\"" + minutesString + \\\"" : \\\"" + secondsString}</span>\\\\n\\\"";\\n            result += $\\\""<input type=''hidden'' name=''timelog_total_seconds'' value=''{loggedSeconds}''/>\\\\n\\\"";\\n            result += $\\\""<input type=''hidden'' name=''timelog_started_on'' value=''{logStartString}''/>\\\"";\\n            return result;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""<span class=\\\""go-gray\\\"" style=''font-size:16px;''>00 : 00 : 00</span>\""}"",
  ""name"": ""field"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 6b80c95e-a06d-4ad3-ae19-dfdc9fecf6ed >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6b80c95e-a06d-4ad3-ae19-dfdc9fecf6ed', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column4', '{
  ""type"": ""0"",
  ""text"": ""set complete"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": ""set-completed"",
  ""id"": """",
  ""icon_class"": ""fa fa-check"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: c0962d97-a609-498b-9b0c-7c0dbfae8b73 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c0962d97-a609-498b-9b0c-7c0dbfae8b73', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 4, 'WebVella.Erp.Web.Components.PcFieldHidden', 'column3', '{
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.is_billable\"",\""default\"":\""\""}"",
  ""name"": ""is_billable""
}');");

            // << ***Create page body node*** Page name: track-time  id: b2baa937-e32a-4a06-8b9b-404f89e539c0 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b2baa937-e32a-4a06-8b9b-404f89e539c0', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column1', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\nusing System.Diagnostics;\\nusing WebVella.Erp.Plugins.Project.Services;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar taskRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord\\\"");\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (taskRecord == null)\\n\\t\\t\\treturn null;\\n\\t\\t\\t\\n        var iconClass = \\\""\\\"";\\n        var color = \\\""\\\"";\\n        new TaskService().GetTaskIconAndColor((string)taskRecord[\\\""priority\\\""], out iconClass, out color);\\n\\n\\t\\treturn $\\\""<i class=''{iconClass}'' style=''color:{color}''></i> <a href=''/projects/tasks/tasks/r/{(Guid)taskRecord[\\\""id\\\""]}/details''>[{(string)taskRecord[\\\""key\\\""]}] {taskRecord[\\\""subject\\\""]}</a>\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""Task name\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: track-time  id: 278b4db1-b310-416a-9f32-66ecd3475ba8 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('278b4db1-b310-416a-9f32-66ecd3475ba8', 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcBtnGroup', 'column3', '{
  ""size"": ""1"",
  ""is_vertical"": ""false"",
  ""class"": ""start-log-group w-100 d-none"",
  ""id"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 55ff1b2f-43d4-4bde-818c-fd139d799261 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('55ff1b2f-43d4-4bde-818c-fd139d799261', '278b4db1-b310-416a-9f32-66ecd3475ba8', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""0"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": ""manual-log"",
  ""id"": """",
  ""icon_class"": ""fa fa-plus"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 4603466b-422c-4666-9f05-aae386569590 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4603466b-422c-4666-9f05-aae386569590', '278b4db1-b310-416a-9f32-66ecd3475ba8', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""0"",
  ""text"": ""start log"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": ""start-log"",
  ""id"": """",
  ""icon_class"": ""fa fa-fw fa-play"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: d32f39bb-8ad4-438d-a8d1-7abca6f5e6b4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d32f39bb-8ad4-438d-a8d1-7abca6f5e6b4', NULL, 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""Projects\""}"",
  ""area_sublabel"": """",
  ""title"": ""Create account"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""App.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-plus"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: create  id: aeacecd8-8b3e-4cdb-84f6-114a2fb3c06d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('aeacecd8-8b3e-4cdb-84f6-114a2fb3c06d', 'd32f39bb-8ad4-438d-a8d1-7abca6f5e6b4', 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""1"",
  ""text"": ""Create Account"",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-fw fa-plus go-green"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""CreateRecord""
}');");

            // << ***Create page body node*** Page name: create  id: b7b8ed33-910f-4d28-bbe8-48c0799b00b5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b7b8ed33-910f-4d28-bbe8-48c0799b00b5', 'd32f39bb-8ad4-438d-a8d1-7abca6f5e6b4', 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""2"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""0\"",\""string\"":\""ReturnUrl\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: 037ee1a4-e26c-4cd1-91ca-0e626c2995ed >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('037ee1a4-e26c-4cd1-91ca-0e626c2995ed', NULL, 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 2, 'WebVella.Erp.Web.Components.PcForm', '', '{
  ""id"": ""CreateRecord"",
  ""name"": ""CreateAccount"",
  ""hook_key"": """",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: create  id: 0fb05f08-6066-4de8-8452-c8b3c7306ff9 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0fb05f08-6066-4de8-8452-c8b3c7306ff9', '037ee1a4-e26c-4cd1-91ca-0e626c2995ed', 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'body', '""{}""');");

            // << ***Create page body node*** Page name: create  id: 5ecb652c-e474-4700-bc32-5173d2fdad00 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5ecb652c-e474-4700-bc32-5173d2fdad00', '0fb05f08-6066-4de8-8452-c8b3c7306ff9', 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": ""Name"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""name"": ""name"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 03d2ed0f-33ed-4b7d-84fb-102f4b7452a8 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('03d2ed0f-33ed-4b7d-84fb-102f4b7452a8', NULL, '80b10445-c850-44cf-9c8c-57daca671dcf', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '""{}""');");

            // << ***Create page body node*** Page name: details  id: 7eb7af4f-bdd3-410a-b3c4-71e620b627c5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7eb7af4f-bdd3-410a-b3c4-71e620b627c5', '03d2ed0f-33ed-4b7d-84fb-102f4b7452a8', '80b10445-c850-44cf-9c8c-57daca671dcf', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": ""Name"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""name"": ""name"",
  ""mode"": ""3"",
  ""maxlength"": 0,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 552a4fad-5236-4aad-b3fc-443a5f12e574 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('552a4fad-5236-4aad-b3fc-443a5f12e574', NULL, '80b10445-c850-44cf-9c8c-57daca671dcf', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""Entity.LabelPlural\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.label\"",\""default\"":\""\""}"",
  ""title"": ""Account Details"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": ""/crm/accounts/accounts/l/list""
}');");

            // << ***Create page body node*** Page name: list  id: 81fda9cf-04d7-4f99-8448-34392e1c0640 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('81fda9cf-04d7-4f99-8448-34392e1c0640', NULL, '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 3, 'WebVella.Erp.Web.Components.PcDrawer', '', '{
  ""title"": ""Search Accounts"",
  ""width"": ""550px"",
  ""class"": """",
  ""body_class"": """",
  ""title_action_html"": ""<a href=\""javascript:void(0)\"" class=\""clear-filter-all\"">clear all</a>""
}');");

            // << ***Create page body node*** Page name: list  id: 492d9088-16bc-40fd-963b-8a8c2acf0ffa >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('492d9088-16bc-40fd-963b-8a8c2acf0ffa', '81fda9cf-04d7-4f99-8448-34392e1c0640', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-492d9088-16bc-40fd-963b-8a8c2acf0ffa"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: list  id: 3845960e-4fc6-40f6-9ef6-36e7392f8ab0 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3845960e-4fc6-40f6-9ef6-36e7392f8ab0', '492d9088-16bc-40fd-963b-8a8c2acf0ffa', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 1, 'WebVella.Erp.Web.Components.PcGridFilterField', 'body', '{
  ""label"": ""Name"",
  ""name"": ""name"",
  ""try_connect_to_entity"": ""true"",
  ""field_type"": ""18"",
  ""query_type"": ""2"",
  ""query_options"": [
    ""2""
  ],
  ""prefix"": """"
}');");

            // << ***Create page body node*** Page name: list  id: ec6f4bb5-aeeb-4706-a3dd-f3f208c63c6a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ec6f4bb5-aeeb-4706-a3dd-f3f208c63c6a', '492d9088-16bc-40fd-963b-8a8c2acf0ffa', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""1"",
  ""text"": ""Search Accounts"",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: all  id: 22af9111-4f15-48c1-a9fd-e5ab72074b3e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('22af9111-4f15-48c1-a9fd-e5ab72074b3e', NULL, '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 4,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""AllProjects\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No projects"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_searchable"": ""false"",
  ""container2_label"": ""abbr"",
  ""container2_width"": ""60px"",
  ""container2_name"": ""abbr"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""true"",
  ""container2_searchable"": ""true"",
  ""container3_label"": ""name"",
  ""container3_width"": """",
  ""container3_name"": ""name"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_searchable"": ""false"",
  ""container4_label"": ""lead"",
  ""container4_width"": """",
  ""container4_name"": ""lead"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_searchable"": ""false"",
  ""container5_label"": ""column5"",
  ""container5_width"": """",
  ""container5_name"": ""column5"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_searchable"": ""false"",
  ""container6_label"": ""column6"",
  ""container6_width"": """",
  ""container6_name"": ""column6"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_searchable"": ""false"",
  ""container7_label"": ""column7"",
  ""container7_width"": """",
  ""container7_name"": ""column7"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_searchable"": ""false"",
  ""container8_label"": ""column8"",
  ""container8_width"": """",
  ""container8_name"": ""column8"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_searchable"": ""false"",
  ""container9_label"": ""column9"",
  ""container9_width"": """",
  ""container9_name"": ""column9"",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_searchable"": ""false"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_searchable"": ""false"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_searchable"": ""false"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_searchable"": ""false""
}');");

            // << ***Create page body node*** Page name: all  id: 31a4f843-0ab5-4fd1-86ee-ad5f23f0d47a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('31a4f843-0ab5-4fd1-86ee-ad5f23f0d47a', '22af9111-4f15-48c1-a9fd-e5ab72074b3e', '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_project_owner[0].username\"",\""default\"":\""Username\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: fcd1e0a0-bfc3-422f-b19d-2536dd919289 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fcd1e0a0-bfc3-422f-b19d-2536dd919289', '22af9111-4f15-48c1-a9fd-e5ab72074b3e', '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.abbr\"",\""default\"":\""abbr\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: ec508ea2-2332-40f0-838c-52d3ee250122 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ec508ea2-2332-40f0-838c-52d3ee250122', '22af9111-4f15-48c1-a9fd-e5ab72074b3e', '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column3', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.name\"",\""default\"":\""Project name\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: 54d22f88-7a46-41e7-89b4-603dc14e7e73 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('54d22f88-7a46-41e7-89b4-603dc14e7e73', '22af9111-4f15-48c1-a9fd-e5ab72074b3e', '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""1"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//replace constants with your values\\n\\t\\tconst string DATASOURCE_NAME = \\\""RowRecord.id\\\"";\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<Guid>(DATASOURCE_NAME);\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (dataSource == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\treturn $\\\""/projects/projects/projects/r/{dataSource}/dashboard\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: 39db266a-da49-4a6e-b74d-898c601ad78b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('39db266a-da49-4a6e-b74d-898c601ad78b', NULL, 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-plus"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: create  id: b5a15dac-a606-4c93-b258-f1a7ab799a05 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b5a15dac-a606-4c93-b258-f1a7ab799a05', '39db266a-da49-4a6e-b74d-898c601ad78b', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""2"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""0\"",\""string\"":\""ReturnUrl\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: 8e4e7f05-8942-4db1-8514-e460bde1e2b4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8e4e7f05-8942-4db1-8514-e460bde1e2b4', '39db266a-da49-4a6e-b74d-898c601ad78b', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""1"",
  ""text"": ""Create Project"",
  ""color"": ""1"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-save"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""CreateRecord""
}');");

            // << ***Create page body node*** Page name: create  id: e6c5b22a-491a-4186-82d6-667253e2db0f >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e6c5b22a-491a-4186-82d6-667253e2db0f', NULL, 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcForm', '', '{
  ""id"": ""CreateRecord"",
  ""name"": ""CreateRecord"",
  ""hook_key"": """",
  ""method"": ""post"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: create  id: 4dfaa373-e250-4a76-b5a5-98d596a52313 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4dfaa373-e250-4a76-b5a5-98d596a52313', 'e6c5b22a-491a-4186-82d6-667253e2db0f', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'body', '{
  ""visible_columns"": 2,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 8,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: create  id: fc423988-297c-457d-a14b-9fe12557cc2e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fc423988-297c-457d-a14b-9fe12557cc2e', '4dfaa373-e250-4a76-b5a5-98d596a52313', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": ""Name"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""name"": ""name"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""1"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: create  id: 0f90af36-8f2d-4f26-8ba2-ea7e8accdc6d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0f90af36-8f2d-4f26-8ba2-ea7e8accdc6d', '4dfaa373-e250-4a76-b5a5-98d596a52313', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column2', '{
  ""label_text"": ""Abbreviation"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.abbr\"",\""default\"":\""\""}"",
  ""name"": ""abbr"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: create  id: 7bbf3667-a26d-48d4-8eba-8ca5e03d14c3 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7bbf3667-a26d-48d4-8eba-8ca5e03d14c3', 'e6c5b22a-491a-4186-82d6-667253e2db0f', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', 'body', '{
  ""visible_columns"": 1,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: create  id: cc487c98-c59f-4e8c-b147-36914bcf70fc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('cc487c98-c59f-4e8c-b147-36914bcf70fc', '7bbf3667-a26d-48d4-8eba-8ca5e03d14c3', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column1', '{
  ""label_text"": ""Description"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.description\"",\""default\"":\""\""}"",
  ""name"": ""description"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: create  id: 6529686c-c8b4-40f0-8242-e24153657be2 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6529686c-c8b4-40f0-8242-e24153657be2', '7bbf3667-a26d-48d4-8eba-8ca5e03d14c3', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', 'column1', '{
  ""visible_columns"": 3,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: create  id: ace9e1bf-47bf-495f-8e6b-7683d2a0fa78 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ace9e1bf-47bf-495f-8e6b-7683d2a0fa78', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column3', '{
  ""label_text"": ""Budget amount"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.budget_amount\"",\""default\"":\""\""}"",
  ""name"": ""budget_amount"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: create  id: aec39a46-526c-45f2-ad43-38618a366098 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('aec39a46-526c-45f2-ad43-38618a366098', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column1', '{
  ""label_text"": ""Start date"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_date\"",\""default\"":\""\""}"",
  ""name"": ""start_date"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 8dc4fd15-a1eb-4b7e-a1a9-381ac8e7de9b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8dc4fd15-a1eb-4b7e-a1a9-381ac8e7de9b', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column3', '{
  ""label_text"": ""Budget type"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.budget_type\"",\""default\"":\""\""}"",
  ""name"": ""budget_type"",
  ""try_connect_to_entity"": ""true"",
  ""options"": """",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 0e6f5387-b9c4-4fdd-9349-73e8424c6788 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0e6f5387-b9c4-4fdd-9349-73e8424c6788', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Account"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.account_id\"",\""default\"":\""\""}"",
  ""name"": ""account_id"",
  ""try_connect_to_entity"": ""false"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllAccountsSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 5c8d449d-95b8-419b-9851-b9a227e7093b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5c8d449d-95b8-419b-9851-b9a227e7093b', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Project lead"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.owner_id\"",\""default\"":\""\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllUsersSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: f0de1f0c-b71d-4002-a547-4e6d08654ea8 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f0de1f0c-b71d-4002-a547-4e6d08654ea8', '6529686c-c8b4-40f0-8242-e24153657be2', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldDate', 'column1', '{
  ""label_text"": ""End date"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_date\"",\""default\"":\""\""}"",
  ""name"": ""end_date"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 7bc83302-6b26-46ef-a6a1-3e656527faef >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7bc83302-6b26-46ef-a6a1-3e656527faef', '7bbf3667-a26d-48d4-8eba-8ca5e03d14c3', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 3, 'WebVella.Erp.Web.Components.PcRow', 'column1', '{
  ""visible_columns"": 3,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: create  id: bf38fcf4-adeb-4388-ad4b-6aa4485f9258 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bf38fcf4-adeb-4388-ad4b-6aa4485f9258', '7bc83302-6b26-46ef-a6a1-3e656527faef', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldCheckbox', 'column1', '{
  ""label_text"": ""Is Billable"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.is_billable\"",\""default\"":\""false\""}"",
  ""name"": ""is_billable"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""text_true"": """",
  ""text_false"": """"
}');");

            // << ***Create page body node*** Page name: create  id: c1e88619-37f4-4dd6-ae9b-f714191d02e3 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c1e88619-37f4-4dd6-ae9b-f714191d02e3', '7bc83302-6b26-46ef-a6a1-3e656527faef', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Billing method"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.billing_method\"",\""default\"":\""\""}"",
  ""name"": ""billing_method"",
  ""try_connect_to_entity"": ""true"",
  ""options"": """",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 50f07e9e-65a5-4feb-bf2a-4f12712305c2 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('50f07e9e-65a5-4feb-bf2a-4f12712305c2', '7bc83302-6b26-46ef-a6a1-3e656527faef', 'c2e38698-24cd-4209-b560-02c225f3ff4a', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column3', '{
  ""label_text"": ""Hour rate"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.hour_rate\"",\""default\"":\""\""}"",
  ""name"": ""hour_rate"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""0"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: details  id: e15e2d00-e704-4212-a7d2-ee125dd687a6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e15e2d00-e704-4212-a7d2-ee125dd687a6', NULL, '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 2,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 8,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 4,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 754bf941-df31-4b13-ba32-eb3c7a8c8922 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('754bf941-df31-4b13-ba32-eb3c7a8c8922', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.subject\"",\""default\"":\""\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""3"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 6e918333-a2fa-4cf7-9ca8-662e349625a7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6e918333-a2fa-4cf7-9ca8-662e349625a7', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 4, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Budget"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""class"": """",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""2"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: details  id: aa94aac4-5048-4d82-95b2-b38536028cbb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('aa94aac4-5048-4d82-95b2-b38536028cbb', '6e918333-a2fa-4cf7-9ca8-662e349625a7', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldNumber', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.estimated_minutes\"",\""default\"":\""\""}"",
  ""name"": ""estimated_minutes"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""3"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 857698b9-f715-480a-bd74-29819a4dec2d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('857698b9-f715-480a-bd74-29819a4dec2d', '6e918333-a2fa-4cf7-9ca8-662e349625a7', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldNumber', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.x_billable_minutes\"",\""default\"":\""\""}"",
  ""name"": ""x_billable_minutes"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""2"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: details  id: ddde395b-6cee-4907-a220-a8424e091b13 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ddde395b-6cee-4907-a220-a8424e091b13', '6e918333-a2fa-4cf7-9ca8-662e349625a7', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 4, 'WebVella.Erp.Web.Components.PcFieldNumber', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.x_nonbillable_minutes\"",\""default\"":\""\""}"",
  ""name"": ""x_nonbillable_minutes"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""2"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: details  id: d076f406-7ddd-4feb-b96a-137e10c2d14e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d076f406-7ddd-4feb-b96a-137e10c2d14e', '6e918333-a2fa-4cf7-9ca8-662e349625a7', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'body', '{
  ""label_text"": ""Project"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""TaskAuxData[0].$project_nn_task[0].name\"",\""default\"":\""Project name\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""2"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 452a6f4c-b415-409a-b9b6-a2918a137299 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('452a6f4c-b415-409a-b9b6-a2918a137299', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 3, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""Activity"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""class"": ""mt-5"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: details  id: 164261ae-2df4-409a-8fdd-adc85c86a6dc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('164261ae-2df4-409a-8fdd-adc85c86a6dc', '452a6f4c-b415-409a-b9b6-a2918a137299', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcTabNav', 'body', '{
  ""visible_tabs"": 3,
  ""render_type"": ""1"",
  ""css_class"": ""mt-4"",
  ""body_css_class"": ""pt-4"",
  ""tab1_label"": ""Comments"",
  ""tab2_label"": ""Feed"",
  ""tab3_label"": ""Timelog"",
  ""tab4_label"": ""Tab 4"",
  ""tab5_label"": ""Tab 5"",
  ""tab6_label"": ""Tab 6"",
  ""tab7_label"": ""Tab 7""
}');");

            // << ***Create page body node*** Page name: details  id: 05459068-33a7-454e-a871-94f9ddc6e5d5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('05459068-33a7-454e-a871-94f9ddc6e5d5', '164261ae-2df4-409a-8fdd-adc85c86a6dc', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcFeedList', 'tab2', '{
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""FeedItemsForRecordId\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: details  id: 8099e123-1218-4008-b8e6-8ff56678d64a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8099e123-1218-4008-b8e6-8ff56678d64a', '164261ae-2df4-409a-8fdd-adc85c86a6dc', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcTimelogList', 'tab3', '{
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""TimeLogsForRecordId\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: details  id: 3e15a63d-8f5f-4357-a692-b5998c31d543 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3e15a63d-8f5f-4357-a692-b5998c31d543', '164261ae-2df4-409a-8fdd-adc85c86a6dc', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcPostList', 'tab1', '{
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""CommentsForRecordId\"",\""default\"":\""\""}"",
  ""mode"": ""comments""
}');");

            // << ***Create page body node*** Page name: details  id: ecc262e9-fbad-4dd1-9c98-56ad047685fb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ecc262e9-fbad-4dd1-9c98-56ad047685fb', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""People"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""class"": ""mb-4"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""2"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: details  id: bbe36a16-9210-415b-95f3-912482d27fd2 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bbe36a16-9210-415b-95f3-912482d27fd2', 'ecc262e9-fbad-4dd1-9c98-56ad047685fb', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.created_by\"",\""default\"":\""\""}"",
  ""name"": ""created_by"",
  ""try_connect_to_entity"": ""true"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllUsersSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""3""
}');");

            // << ***Create page body node*** Page name: details  id: 101245d5-1ff9-4eb3-ba28-0b29cb56a0ec >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('101245d5-1ff9-4eb3-ba28-0b29cb56a0ec', 'ecc262e9-fbad-4dd1-9c98-56ad047685fb', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body', '{
  ""label_text"": ""Owner"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.owner_id\"",\""default\"":\""\""}"",
  ""name"": ""owner_id"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllUsersSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: f2175b92-4941-4cbe-ba4b-305167b6738b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f2175b92-4941-4cbe-ba4b-305167b6738b', 'ecc262e9-fbad-4dd1-9c98-56ad047685fb', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 4, 'WebVella.Erp.Web.Components.PcHtmlBlock', 'body', '{
  ""html"": ""{\""type\"":\""2\"",\""string\"":\""<script src=\\\""/api/v3.0/p/project/files/javascript?file=task-details.js\\\"" type=\\\""text/javascript\\\""></script>\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: details  id: 9f15bb3a-b6bf-424c-9394-669cc2041215 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9f15bb3a-b6bf-424c-9394-669cc2041215', 'ecc262e9-fbad-4dd1-9c98-56ad047685fb', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldHtml', 'body', '{
  ""label_text"": ""Watchers"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\nusing System.Linq;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\n\\t\\t\\tvar taskAuxData = pageModel.TryGetDataSourceProperty<EntityRecordList>(\\\""TaskAuxData\\\"");\\n\\t        var currentUser = pageModel.TryGetDataSourceProperty<ErpUser>(\\\""CurrentUser\\\"");\\n\\t        var recordId = pageModel.TryGetDataSourceProperty<Guid>(\\\""RecordId\\\"");\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (taskAuxData == null && !taskAuxData.Any())\\n\\t\\t\\t\\treturn \\\""\\\"";\\n\\t        var watcherIdList = new List<Guid>();\\n\\t        if(taskAuxData[0].Properties.ContainsKey(\\\""$user_nn_task_watchers\\\"") && taskAuxData[0][\\\""$user_nn_task_watchers\\\""] != null \\n\\t            && taskAuxData[0][\\\""$user_nn_task_watchers\\\""] is List<EntityRecord>){\\n\\t                watcherIdList = ((List<EntityRecord>)taskAuxData[0][\\\""$user_nn_task_watchers\\\""]).Select(x=> (Guid)x[\\\""id\\\""]).ToList();\\n\\t            }\\n\\t        var watcherCount = watcherIdList.Count;\\n\\t        var currentUserIsWatching = false;\\n\\t        if(currentUser != null && watcherIdList.Contains(currentUser.Id))\\n\\t            currentUserIsWatching = true;\\n\\t\\n\\t        var html = $\\\""<span class=''badge go-bkg-blue-gray-light mr-3''>{watcherCount}</span>\\\"";\\n\\t        if(currentUserIsWatching)\\n\\t            html += \\\""<a href=\\\\\\\""#\\\\\\\"" onclick=\\\\\\\""StopTaskWatch(''\\\"" + recordId + \\\""'')\\\\\\\"">stop watching</a>\\\"";\\n\\t        else\\n\\t            html += \\\""<a href=\\\\\\\""#\\\\\\\"" onclick=\\\\\\\""StartTaskWatch(''\\\"" + recordId + \\\""'')\\\\\\\"">start watching</a>\\\"";\\n\\t\\n\\t\\t\\treturn html;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""mode"": ""2"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 27843f6e-43ed-49e7-9cc5-ec35393e93f4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('27843f6e-43ed-49e7-9cc5-ec35393e93f4', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column1', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.body\"",\""default\"":\""\""}"",
  ""name"": ""body"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""3"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: details  id: 651e5fb2-56df-4c46-86b3-19a641dc942d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('651e5fb2-56df-4c46-86b3-19a641dc942d', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 3, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Dates"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""class"": ""mb-4"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""2"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: details  id: 798e39b3-7a36-406b-bed6-e77da68fc50f >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('798e39b3-7a36-406b-bed6-e77da68fc50f', '651e5fb2-56df-4c46-86b3-19a641dc942d', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDateTime', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 291477ec-dd9c-4fc3-97a0-d2fd62809b2f >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('291477ec-dd9c-4fc3-97a0-d2fd62809b2f', '651e5fb2-56df-4c46-86b3-19a641dc942d', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldDateTime', 'body', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: b2935724-bfcc-4821-bdb2-81bc9b14f015 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b2935724-bfcc-4821-bdb2-81bc9b14f015', '651e5fb2-56df-4c46-86b3-19a641dc942d', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 5, 'WebVella.Erp.Web.Components.PcFieldDateTime', 'body', '{
  ""label_text"": ""Created on"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.created_on\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: b105d13c-3710-4ace-b51f-b57323912524 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b105d13c-3710-4ace-b51f-b57323912524', 'e15e2d00-e704-4212-a7d2-ee125dd687a6', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Details"",
  ""title_tag"": ""h3"",
  ""is_card"": ""false"",
  ""class"": ""mb-4"",
  ""body_class"": """",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: details  id: 70a864dc-8311-4dd3-bc13-1a3b87821e30 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('70a864dc-8311-4dd3-bc13-1a3b87821e30', 'b105d13c-3710-4ace-b51f-b57323912524', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body', '{
  ""label_text"": ""Status"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.status_id\"",\""default\"":\""\""}"",
  ""name"": ""status_id"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""TaskStatusesSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 1fff4a92-d045-4019-b27c-bccb1fd1cb82 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('1fff4a92-d045-4019-b27c-bccb1fd1cb82', 'b105d13c-3710-4ace-b51f-b57323912524', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body', '{
  ""label_text"": ""Type"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.type_id\"",\""default\"":\""\""}"",
  ""name"": ""type_id"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""TaskTypesSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: ee526509-7840-498a-9c1f-8a69d80c5f2e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ee526509-7840-498a-9c1f-8a69d80c5f2e', 'b105d13c-3710-4ace-b51f-b57323912524', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldSelect', 'body', '{
  ""label_text"": ""Priority"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.priority\"",\""default\"":\""\""}"",
  ""name"": ""priority"",
  ""options"": """",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: dashboard  id: 6f8f9a9a-a464-4175-9178-246b792738a6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6f8f9a9a-a464-4175-9178-246b792738a6', NULL, '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.abbr\"",\""default\"":\""\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-tachometer-alt"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: dashboard  id: 3ad8d6e5-eed7-44b7-95e1-12f22714037b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3ad8d6e5-eed7-44b7-95e1-12f22714037b', '6f8f9a9a-a464-4175-9178-246b792738a6', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'toolbar', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\tvar projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""Record.id\\\"");\\n        var pageName = pageModel.TryGetDataSourceProperty<string>(\\\""Page.Name\\\"");\\n\\n\\t\\tif (projectId == null || pageName == null)\\n\\t\\t\\treturn null;\\n\\n        var result = $\\\""<a href=''/projects/projects/projects/r/{projectId}/dashboard'' class=''btn btn-link btn-sm {(pageName == \\\""dashboard\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Dashboard</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/feed'' class=''btn btn-link btn-sm {(pageName == \\\""feed\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Feed</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks'' class=''btn btn-link btn-sm {(pageName == \\\""tasks\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Tasks</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/55c8d6e2-f26d-4689-9d1b-a8c1b9de1672/l/milestones'' class=''btn btn-link btn-sm {(pageName == \\\""milestones\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Milestones</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/details'' class=''btn btn-link btn-sm {(pageName == \\\""details\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Details</a>\\\"";\\n\\t\\treturn result;\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: all  id: 89cb4088-ea04-4ce2-8cbe-5367c5741ef3 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('89cb4088-ea04-4ce2-8cbe-5367c5741ef3', NULL, '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: all  id: 8cbdfd1a-5d0e-4961-8e79-74072c133202 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8cbdfd1a-5d0e-4961-8e79-74072c133202', '89cb4088-ea04-4ce2-8cbe-5367c5741ef3', '57db749f-e69e-4d88-b9d1-66203da05da1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""2"",
  ""text"": ""Create Project"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-plus go-green"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""/projects/projects/projects/c"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 2d3dddf7-cefb-4073-977f-4e1b6bf8935e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('2d3dddf7-cefb-4073-977f-4e1b6bf8935e', NULL, '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 5, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 3,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 747c108b-ed45-46f3-b06a-113e2490888d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('747c108b-ed45-46f3-b06a-113e2490888d', '2d3dddf7-cefb-4073-977f-4e1b6bf8935e', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column3', '{
  ""label_text"": ""Hour rate"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.hour_rate\"",\""default\"":\""\""}"",
  ""name"": ""hour_rate"",
  ""mode"": ""3"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: df7c7cab-0e16-4e75-bb13-04666afeff81 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('df7c7cab-0e16-4e75-bb13-04666afeff81', '2d3dddf7-cefb-4073-977f-4e1b6bf8935e', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Billing method"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.billing_method\"",\""default\"":\""\""}"",
  ""name"": ""billing_method"",
  ""options"": """",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 302de86f-7178-4e2b-9ac1-d447163a9558 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('302de86f-7178-4e2b-9ac1-d447163a9558', '2d3dddf7-cefb-4073-977f-4e1b6bf8935e', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldCheckbox', 'column1', '{
  ""label_text"": ""Is Billable"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.is_billable\"",\""default\"":\""\""}"",
  ""name"": ""is_billable"",
  ""mode"": ""3"",
  ""text_true"": """",
  ""text_false"": """",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: tasks  id: 86506ad8-e1cb-4b46-84b9-881e0326ebaa >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('86506ad8-e1cb-4b46-84b9-881e0326ebaa', NULL, '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""Projects\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""ParentRecord.abbr\"",\""default\"":\""Abbr\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""ParentRecord.name\"",\""default\"":\""Project name\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""ParentEntity.Color\"",\""default\"":\""#9c27b0\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""ParentEntity.IconName\"",\""default\"":\""fa fa-file\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: tasks  id: 94bfe723-e5f6-478d-afb6-504edf2bdc2b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('94bfe723-e5f6-478d-afb6-504edf2bdc2b', '86506ad8-e1cb-4b46-84b9-881e0326ebaa', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
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
  ""onclick"": ""ErpEvent.DISPATCH(''WebVella.Erp.Web.Components.PcDrawer'',''open'')"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: tasks  id: 5891b0d8-6750-4502-bd8e-fe1380f08b0c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5891b0d8-6750-4502-bd8e-fe1380f08b0c', '86506ad8-e1cb-4b46-84b9-881e0326ebaa', '6f673561-fad7-4844-8262-589834f1b2ce', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'toolbar', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\tvar projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""ParentRecord.id\\\"");\\n        var pageName = pageModel.TryGetDataSourceProperty<string>(\\\""Page.Name\\\"");\\n\\n\\t\\tif (projectId == null || pageName == null)\\n\\t\\t\\treturn null;\\n\\n        var result = $\\\""<a href=''/projects/projects/projects/r/{projectId}/dashboard'' class=''btn btn-link btn-sm {(pageName == \\\""dashboard\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Dashboard</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/feed'' class=''btn btn-link btn-sm {(pageName == \\\""feed\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Feed</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks'' class=''btn btn-link btn-sm {(pageName == \\\""tasks\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Tasks</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/55c8d6e2-f26d-4689-9d1b-a8c1b9de1672/l/milestones'' class=''btn btn-link btn-sm {(pageName == \\\""milestones\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Milestones</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/details'' class=''btn btn-link btn-sm {(pageName == \\\""details\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Details</a>\\\"";\\n\\t\\treturn result;\\n\\t}\\n}\\n\"",\""default\"":\""Project sub navigation\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: details  id: 94be4b02-07ea-4a54-a6fc-89316fa1e90a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('94be4b02-07ea-4a54-a6fc-89316fa1e90a', NULL, '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.abbr\"",\""default\"":\""\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""ParentEntity.Color\"",\""default\"":\""#9c27b0\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-info"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 0c295451-1c38-4eb0-8000-feefe912a667 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0c295451-1c38-4eb0-8000-feefe912a667', '94be4b02-07ea-4a54-a6fc-89316fa1e90a', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'toolbar', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\tvar projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""Record.id\\\"");\\n        var pageName = pageModel.TryGetDataSourceProperty<string>(\\\""Page.Name\\\"");\\n\\n\\t\\tif (projectId == null || pageName == null)\\n\\t\\t\\treturn null;\\n\\n        var result = $\\\""<a href=''/projects/projects/projects/r/{projectId}/dashboard'' class=''btn btn-link btn-sm {(pageName == \\\""dashboard\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Dashboard</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/feed'' class=''btn btn-link btn-sm {(pageName == \\\""feed\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Feed</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks'' class=''btn btn-link btn-sm {(pageName == \\\""tasks\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Tasks</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/55c8d6e2-f26d-4689-9d1b-a8c1b9de1672/l/milestones'' class=''btn btn-link btn-sm {(pageName == \\\""milestones\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Milestones</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/details'' class=''btn btn-link btn-sm {(pageName == \\\""details\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Details</a>\\\"";\\n\\t\\treturn result;\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: feed  id: 2684f725-38e2-4f8c-92ee-e3b1ccf04aff >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('2684f725-38e2-4f8c-92ee-e3b1ccf04aff', NULL, 'acb76466-32b8-428c-81cb-47b6013879e7', NULL, 4, 'WebVella.Erp.Plugins.Project.Components.PcFeedList', '', '{
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""FeedItemsForRecordId\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: details  id: 6029e40b-0835-460f-b782-1e4228ea4234 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6029e40b-0835-460f-b782-1e4228ea4234', NULL, '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 2,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 8,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: details  id: be6aa619-e380-4bf9-b279-47dda4d5f4eb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('be6aa619-e380-4bf9-b279-47dda4d5f4eb', '6029e40b-0835-460f-b782-1e4228ea4234', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column1', '{
  ""label_text"": ""Description"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.description\"",\""default\"":\""\""}"",
  ""name"": ""description"",
  ""mode"": ""3"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 0dbdb202-7288-49e6-b922-f69e947590e5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0dbdb202-7288-49e6-b922-f69e947590e5', '6029e40b-0835-460f-b782-1e4228ea4234', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column2', '{
  ""label_text"": ""Abbreviation"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.abbr\"",\""default\"":\""\""}"",
  ""name"": ""abbr"",
  ""mode"": ""2"",
  ""maxlength"": 0,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 7f01d2c0-2542-4b88-b8f0-711947e4d0c6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7f01d2c0-2542-4b88-b8f0-711947e4d0c6', '6029e40b-0835-460f-b782-1e4228ea4234', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": ""Name"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.name\"",\""default\"":\""\""}"",
  ""name"": ""name"",
  ""mode"": ""3"",
  ""maxlength"": 0,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: milestones  id: fac5a2f6-b1b4-402a-bf0d-e0a3fb4dd36a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fac5a2f6-b1b4-402a-bf0d-e0a3fb4dd36a', NULL, 'd07cbf70-09c6-47ee-9a13-80568e43d331', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""Projects\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""ParentRecord.abbr\"",\""default\"":\""abbr\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""ParentRecord.name\"",\""default\"":\""Project name\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""ParentEntity.Color\"",\""default\"":\""#9c27b0\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""fa fa-file\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: milestones  id: 4a059596-3804-435e-b535-2da1f56abb29 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4a059596-3804-435e-b535-2da1f56abb29', 'fac5a2f6-b1b4-402a-bf0d-e0a3fb4dd36a', 'd07cbf70-09c6-47ee-9a13-80568e43d331', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'toolbar', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\tvar projectId = pageModel.TryGetDataSourceProperty<Guid>(\\\""ParentRecord.id\\\"");\\n        var pageName = pageModel.TryGetDataSourceProperty<string>(\\\""Page.Name\\\"");\\n\\n\\t\\tif (projectId == null || pageName == null)\\n\\t\\t\\treturn null;\\n\\n        var result = $\\\""<a href=''/projects/projects/projects/r/{projectId}/dashboard'' class=''btn btn-link btn-sm {(pageName == \\\""dashboard\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Dashboard</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/feed'' class=''btn btn-link btn-sm {(pageName == \\\""feed\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Feed</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/b1db4466-7423-44e9-b6b9-3063222c9e15/l/tasks'' class=''btn btn-link btn-sm {(pageName == \\\""tasks\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Tasks</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/rl/55c8d6e2-f26d-4689-9d1b-a8c1b9de1672/l/milestones'' class=''btn btn-link btn-sm {(pageName == \\\""milestones\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Milestones</a>\\\"";\\n        result += $\\\""<a href=''/projects/projects/projects/r/{projectId}/details'' class=''btn btn-link btn-sm {(pageName == \\\""details\\\"" ? \\\""active\\\"" : \\\""\\\"")}''>Details</a>\\\"";\\n\\t\\treturn result;\\n\\t}\\n}\\n\"",\""default\"":\""Project subnavigation\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: no-owner  id: edc68b26-d508-4c2e-a431-5a6656957944 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('edc68b26-d508-4c2e-a431-5a6656957944', NULL, 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: 719ca43f-bb66-4134-a2d2-ad2cc30ade6d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('719ca43f-bb66-4134-a2d2-ad2cc30ade6d', 'edc68b26-d508-4c2e-a431-5a6656957944', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
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
  ""onclick"": ""ErpEvent.DISPATCH(''WebVella.Erp.Web.Components.PcDrawer'',''open'')"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: b6951134-f57f-4da2-8203-a8c36cc99fd7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b6951134-f57f-4da2-8203-a8c36cc99fd7', NULL, '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""Projects\""}"",
  ""area_sublabel"": """",
  ""title"": ""Create task"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""App.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-plus"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: create  id: bd3ed9ae-90aa-4373-9eb9-cc677353bc6d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bd3ed9ae-90aa-4373-9eb9-cc677353bc6d', 'b6951134-f57f-4da2-8203-a8c36cc99fd7', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""1"",
  ""text"": ""Create task"",
  ""color"": ""1"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-save"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""CreateRecord""
}');");

            // << ***Create page body node*** Page name: create  id: 48105732-6025-4614-9065-55647afa9b96 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('48105732-6025-4614-9065-55647afa9b96', 'b6951134-f57f-4da2-8203-a8c36cc99fd7', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
  ""type"": ""2"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""0\"",\""string\"":\""ReturnUrl\"",\""default\"":\""/projects/dashboard/dashboard/a/dashboard\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: create  id: 1af3c0cb-a58e-4d19-89a2-2ce4b8e60945 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('1af3c0cb-a58e-4d19-89a2-2ce4b8e60945', NULL, '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 2, 'WebVella.Erp.Web.Components.PcForm', '', '{
  ""id"": ""CreateRecord"",
  ""name"": ""CreateRecord"",
  ""hook_key"": """",
  ""method"": ""post"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: create  id: a1110167-15bd-46b7-ae3c-cc8ba87be98f >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a1110167-15bd-46b7-ae3c-cc8ba87be98f', '1af3c0cb-a58e-4d19-89a2-2ce4b8e60945', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'body', '{
  ""visible_columns"": 2,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 8,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: create  id: 884a8db1-aff0-4f86-ab7d-8fb17698fc33 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('884a8db1-aff0-4f86-ab7d-8fb17698fc33', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 8, 'WebVella.Erp.Web.Components.PcFieldDateTime', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_time\"",\""default\"":\""\""}"",
  ""name"": ""start_time"",
  ""mode"": ""0"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: create  id: ecef4b2c-6988-44c1-acea-0e28385ec528 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ecef4b2c-6988-44c1-acea-0e28385ec528', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 9, 'WebVella.Erp.Web.Components.PcFieldDateTime', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_time\"",\""default\"":\""\""}"",
  ""name"": ""end_time"",
  ""mode"": ""0"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: create  id: 4a588a7d-ea03-4be1-ab0d-3120d98c3548 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4a588a7d-ea03-4be1-ab0d-3120d98c3548', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 5, 'WebVella.Erp.Web.Components.PcFieldHidden', 'column2', '{
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\nusing System.Globalization;\\n\\npublic class SampleCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\treturn DateTime.UtcNow.ToString(\\\""o\\\"", CultureInfo.InvariantCulture);\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""created_on"",
  ""try_connect_to_entity"": ""false""
}');");

            // << ***Create page body node*** Page name: create  id: e03e40c2-dae2-4351-947c-02295a064328 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e03e40c2-dae2-4351-947c-02295a064328', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Type Id"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.type_id\"",\""default\"":\""a0465e9f-5d5f-433d-acf1-1da0eaec78b4\""}"",
  ""name"": ""type_id"",
  ""try_connect_to_entity"": ""true"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""TaskTypeSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: 23200f02-439a-4719-ae0e-498c9dcde58c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('23200f02-439a-4719-ae0e-498c9dcde58c', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Owner"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.owner_id\"",\""default\"":\""\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllUsersSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: create  id: fb3d0142-e080-43ef-ba31-a79d0221c0df >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fb3d0142-e080-43ef-ba31-a79d0221c0df', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column1', '{
  ""label_text"": ""Subject"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.subject\"",\""default\"":\""\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: create  id: fe59f151-1e79-4df2-a034-9f45f1dcd691 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fe59f151-1e79-4df2-a034-9f45f1dcd691', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column1', '{
  ""label_text"": ""Description"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.body\"",\""default\"":\""\""}"",
  ""name"": ""body"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""3""
}');");

            // << ***Create page body node*** Page name: create  id: 30e99568-2727-4ce4-8da6-c97feaaf4432 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('30e99568-2727-4ce4-8da6-c97feaaf4432', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 4, 'WebVella.Erp.Web.Components.PcFieldHidden', 'column2', '{
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""CurrentUser.Id\"",\""default\"":\""\""}"",
  ""name"": ""created_by"",
  ""try_connect_to_entity"": ""false""
}');");

            // << ***Create page body node*** Page name: create  id: 1739a2f0-76ba-4343-a344-9b0564096d06 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('1739a2f0-76ba-4343-a344-9b0564096d06', 'a1110167-15bd-46b7-ae3c-cc8ba87be98f', '68100014-1fd7-456c-9b26-27aa9f858287', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Project"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry{\\n\\t\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\t\\tif (pageModel == null)\\n\\t\\t\\t\\treturn null;\\n\\t\\n\\t\\t\\t//try read data source by name and get result as specified type object\\n\\t\\t\\tvar record = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n\\n\\t\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\t\\tif (record == null)\\n\\t\\t\\t\\treturn null;\\n\\n            if(record.Properties.ContainsKey(\\\""$project_nn_task.id\\\"")){\\n                var relationObject = record[\\\""$project_nn_task.id\\\""];\\n                if(relationObject is List<Guid> && ((List<Guid>)relationObject).Count > 0){\\n                    return ((List<Guid>)relationObject)[0];\\n                }\\n            }\\n\\t\\t\\treturn record;\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""name"": ""$project_nn_task.id"",
  ""try_connect_to_entity"": ""false"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllProjectsSelectOption\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: all  id: 7612914f-21ea-4665-9b66-385cf1cafb41 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7612914f-21ea-4665-9b66-385cf1cafb41', NULL, '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: all  id: d7ef95ce-8508-4722-a5f0-3d114bda4585 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d7ef95ce-8508-4722-a5f0-3d114bda4585', '7612914f-21ea-4665-9b66-385cf1cafb41', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
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
  ""onclick"": ""ErpEvent.DISPATCH(''WebVella.Erp.Web.Components.PcDrawer'',''open'')"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: dashboard  id: 7e2d1d10-a9cc-4eae-b3d6-a30ab3647102 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', NULL, '50e4e84d-4148-4635-8372-4f2262747668', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 2,
  ""class"": ""mb-3"",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: dashboard  id: 66828292-07c7-4cc1-9060-a92798d6b95a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('66828292-07c7-4cc1-9060-a92798d6b95a', '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 2, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""Timesheet"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 6bf0435c-f9c0-44b7-a801-00222cd7c0bb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6bf0435c-f9c0-44b7-a801-00222cd7c0bb', '66828292-07c7-4cc1-9060-a92798d6b95a', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTimesheet', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 0c29732a-945e-4bbc-9486-b86efb2897b2 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0c29732a-945e-4bbc-9486-b86efb2897b2', '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'column1', '{
  ""visible_columns"": 2,
  ""class"": ""mb-3"",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: dashboard  id: a94793d2-492a-4b7e-9fac-199f8bf46f46 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a94793d2-492a-4b7e-9fac-199f8bf46f46', '0c29732a-945e-4bbc-9486-b86efb2897b2', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""Tasks"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: bcf153c5-8c7c-4ac8-a5d9-04e288ff7ccf >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bcf153c5-8c7c-4ac8-a5d9-04e288ff7ccf', 'a94793d2-492a-4b7e-9fac-199f8bf46f46', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksChart', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 5466f4d1-20a5-4808-8bb5-aefaac756347 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5466f4d1-20a5-4808-8bb5-aefaac756347', '0c29732a-945e-4bbc-9486-b86efb2897b2', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Budget"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm h-100"",
  ""body_class"": ""p-3 align-center-col "",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 4c58c1bb-321a-41c6-954f-4c6fafe6661c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('4c58c1bb-321a-41c6-954f-4c6fafe6661c', '5466f4d1-20a5-4808-8bb5-aefaac756347', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetBudgetChart', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 734e7201-a15e-4ae5-8ea9-b683a94f80d0 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('734e7201-a15e-4ae5-8ea9-b683a94f80d0', '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 2, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Tasks Due Today"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm"",
  ""body_class"": ""pb-3 pt-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 8fbba43e-cd4d-4b0d-9e3e-7d19a2cc8468 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8fbba43e-cd4d-4b0d-9e3e-7d19a2cc8468', '734e7201-a15e-4ae5-8ea9-b683a94f80d0', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksQueue', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}"",
  ""user_id"": """",
  ""type"": ""2""
}');");

            // << ***Create page body node*** Page name: dashboard  id: f1c53374-0efb-4612-ab94-68e8e8242ddb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('f1c53374-0efb-4612-ab94-68e8e8242ddb', '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 3, 'WebVella.Erp.Web.Components.PcSection', 'column1', '{
  ""title"": ""Task distribution"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pt-3 pb-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: becc4486-be49-4fa6-9d3a-0d8e15606fcc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('becc4486-be49-4fa6-9d3a-0d8e15606fcc', 'f1c53374-0efb-4612-ab94-68e8e8242ddb', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTaskDistribution', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 39fa86aa-3d7a-49af-bfa9-30f1c03671eb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('39fa86aa-3d7a-49af-bfa9-30f1c03671eb', '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 1, 'WebVella.Erp.Web.Components.PcSection', 'column2', '{
  ""title"": ""Overdue tasks"",
  ""title_tag"": ""strong"",
  ""is_card"": ""true"",
  ""class"": ""card-sm mb-3"",
  ""body_class"": ""pb-3 pt-3"",
  ""is_collapsable"": ""false"",
  ""label_mode"": ""1"",
  ""field_mode"": ""1"",
  ""is_collapsed"": ""false""
}');");

            // << ***Create page body node*** Page name: dashboard  id: 27a635a7-143c-4a28-bf08-601771a453c1 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('27a635a7-143c-4a28-bf08-601771a453c1', '39fa86aa-3d7a-49af-bfa9-30f1c03671eb', '50e4e84d-4148-4635-8372-4f2262747668', NULL, 2, 'WebVella.Erp.Plugins.Project.Components.PcProjectWidgetTasksQueue', 'body', '{
  ""project_id"": ""{\""type\"":\""0\"",\""string\"":\""Record.id\"",\""default\"":\""\""}"",
  ""user_id"": """",
  ""type"": ""1""
}');");

            // << ***Create page body node*** Page name: all  id: cb0e42ee-aa06-4a92-8bb0-940e7332411e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('cb0e42ee-aa06-4a92-8bb0-940e7332411e', NULL, '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 3, 'WebVella.Erp.Web.Components.PcDrawer', '', '{
  ""title"": ""Search Tasks"",
  ""width"": ""550px"",
  ""class"": """",
  ""body_class"": """",
  ""title_action_html"": """"
}');");

            // << ***Create page body node*** Page name: all  id: 156877b1-d1ea-4fea-be4a-62a982bef3a7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('156877b1-d1ea-4fea-be4a-62a982bef3a7', 'cb0e42ee-aa06-4a92-8bb0-940e7332411e', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-156877b1-d1ea-4fea-be4a-62a982bef3a7"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: all  id: bef8058a-2c62-47a8-abd3-813636ebd4a8 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bef8058a-2c62-47a8-abd3-813636ebd4a8', '156877b1-d1ea-4fea-be4a-62a982bef3a7', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""1"",
  ""text"": ""Search Tasks"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: all  id: 319c5697-21c9-4799-ae6f-343586f5d2cf >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('319c5697-21c9-4799-ae6f-343586f5d2cf', '156877b1-d1ea-4fea-be4a-62a982bef3a7', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcGridFilterField', 'body', '{
  ""label"": ""Task contents"",
  ""name"": ""x_search"",
  ""try_connect_to_entity"": ""true"",
  ""field_type"": ""18"",
  ""query_type"": ""2"",
  ""query_options"": [
    ""2""
  ],
  ""prefix"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 630bea4c-bccf-4587-83f7-6d0d2ed5bac0 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('630bea4c-bccf-4587-83f7-6d0d2ed5bac0', NULL, '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": ""{\""type\"":\""0\"",\""string\"":\""Record.key\"",\""default\"":\""NXT-1\""}"",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Record.subject\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Linq;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar record = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n        var taskTypes = pageModel.TryGetDataSourceProperty<EntityRecordList>(\\\""TaskTypes\\\"");\\n        \\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (record == null || !record.Properties.ContainsKey(\\\""type_id\\\"") || taskTypes == null)\\n\\t\\t\\treturn null;\\n\\n        var taskType = taskTypes.FirstOrDefault(x=> (Guid)x[\\\""id\\\""] == (Guid)record[\\\""type_id\\\""]);\\n        if(taskType != null && taskType.Properties.ContainsKey(\\\""color\\\"") && !String.IsNullOrWhiteSpace((string)taskType[\\\""color\\\""])){\\n            return (string)taskType[\\\""color\\\""];\\n        }\\n\\n\\t\\treturn null;\\n\\t}\\n}\\n\"",\""default\"":\""#999\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Linq;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar record = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""Record\\\"");\\n        var taskTypes = pageModel.TryGetDataSourceProperty<EntityRecordList>(\\\""TaskTypes\\\"");\\n        \\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (record == null || !record.Properties.ContainsKey(\\\""type_id\\\"") || taskTypes == null)\\n\\t\\t\\treturn null;\\n\\n        var taskType = taskTypes.FirstOrDefault(x=> (Guid)x[\\\""id\\\""] == (Guid)record[\\\""type_id\\\""]);\\n        if(taskType != null && taskType.Properties.ContainsKey(\\\""icon_class\\\"") && !String.IsNullOrWhiteSpace((string)taskType[\\\""icon_class\\\""])){\\n            return (string)taskType[\\\""icon_class\\\""];\\n        }\\n\\n\\t\\treturn null;\\n\\t}\\n}\\n\"",\""default\"":\""fas fa-user-cog\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: b14bb20c-fab7-40a4-8feb-8a899b761dda >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b14bb20c-fab7-40a4-8feb-8a899b761dda', NULL, 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 3, 'WebVella.Erp.Web.Components.PcHtmlBlock', '', '{
  ""html"": ""{\""type\"":\""2\"",\""string\"":\""<script src=\\\""/api/v3.0/p/project/files/javascript?file=timetrack.js\\\"" type=\\\""text/javascript\\\""></script>\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: ca7a9302-afc3-4688-9748-676211bcddb3 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ca7a9302-afc3-4688-9748-676211bcddb3', NULL, 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""#9C27B0"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-database"",
  ""return_url"": ""/projects/reports/list/a/list""
}');");

            // << ***Create page body node*** Page name: list  id: a066475d-c2ff-4e59-9481-08cd637f71ca >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a066475d-c2ff-4e59-9481-08cd637f71ca', NULL, '84b892fc-6ca4-4c7e-8b7c-2f2f6954862f', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""false"",
  ""color"": ""#9C27B0"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""fa fa-database"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4', NULL, 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 5, 'WebVella.Erp.Web.Components.PcModal', '', '{
  ""title"": ""Create timelog"",
  ""backdrop"": ""true"",
  ""size"": ""2"",
  ""position"": ""0""
}');");

            // << ***Create page body node*** Page name: track-time  id: 25ac7fb9-2737-428d-9678-90222252c024 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('25ac7fb9-2737-428d-9678-90222252c024', '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'footer', '{
  ""type"": ""1"",
  ""text"": ""Create log"",
  ""color"": ""19"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-plus"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": ""wv-timetrack-log""
}');");

            // << ***Create page body node*** Page name: track-time  id: d6b5ad6d-4455-4828-bc46-b072aa4919f5 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d6b5ad6d-4455-4828-bc46-b072aa4919f5', '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-timetrack-log"",
  ""name"": ""TimeTrackCreateLog"",
  ""hook_key"": ""TimeTrackCreateLog"",
  ""method"": ""post"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: track-time  id: 3658981b-cef7-4938-9c3a-a13cd5b760a0 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3658981b-cef7-4938-9c3a-a13cd5b760a0', 'd6b5ad6d-4455-4828-bc46-b072aa4919f5', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'body', '{
  ""visible_columns"": 4,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: track-time  id: cd70f0e3-be35-4f94-8894-c8f26e021d88 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('cd70f0e3-be35-4f94-8894-c8f26e021d88', '3658981b-cef7-4938-9c3a-a13cd5b760a0', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": ""Log started on"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""timelog_started_on"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""1"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: track-time  id: 6b3e9fec-7fc1-4455-8dcc-a3b67f4ca427 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6b3e9fec-7fc1-4455-8dcc-a3b67f4ca427', '3658981b-cef7-4938-9c3a-a13cd5b760a0', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column1', '{
  ""label_text"": ""Log Date"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""CurrentDate\"",\""default\"":\""\""}"",
  ""name"": ""logged_on"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: track-time  id: 6b0ee717-f4af-47fb-b441-125a755af01b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6b0ee717-f4af-47fb-b441-125a755af01b', '3658981b-cef7-4938-9c3a-a13cd5b760a0', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldCheckbox', 'column3', '{
  ""label_text"": ""Billable"",
  ""label_mode"": ""0"",
  ""value"": ""true"",
  ""name"": ""is_billable"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0"",
  ""text_true"": ""billable time"",
  ""text_false"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 9dcca796-cb6d-4c7f-bb63-761cff4c218a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9dcca796-cb6d-4c7f-bb63-761cff4c218a', '3658981b-cef7-4938-9c3a-a13cd5b760a0', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column2', '{
  ""label_text"": ""Logged Minutes"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""minutes"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0
}');");

            // << ***Create page body node*** Page name: track-time  id: d11a258c-2ad3-4421-84db-990aa7683a2d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d11a258c-2ad3-4421-84db-990aa7683a2d', 'd6b5ad6d-4455-4828-bc46-b072aa4919f5', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldHidden', 'body', '{
  ""value"": """",
  ""name"": ""task_id"",
  ""try_connect_to_entity"": ""false""
}');");

            // << ***Create page body node*** Page name: track-time  id: 418b58a8-88d2-4dfc-b4a9-22617dab76c4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('418b58a8-88d2-4dfc-b4a9-22617dab76c4', 'd6b5ad6d-4455-4828-bc46-b072aa4919f5', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldTextarea', 'body', '{
  ""label_text"": ""Description"",
  ""label_mode"": ""0"",
  ""value"": """",
  ""name"": ""body"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""0"",
  ""height"": """"
}');");

            // << ***Create page body node*** Page name: track-time  id: 9946104a-a6ec-4a0b-b996-7bc630c16287 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9946104a-a6ec-4a0b-b996-7bc630c16287', '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'footer', '{
  ""type"": ""0"",
  ""text"": ""Cancel"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": ""ErpEvent.DISPATCH(''WebVella.Erp.Web.Components.PcModal'',{htmlId:''wv-6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4'',action:''close'',payload:null})"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: ffac14be-00ee-4a72-a08e-f5b0956171c4 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ffac14be-00ee-4a72-a08e-f5b0956171c4', NULL, 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 2, 'WebVella.Erp.Web.Components.PcForm', '', '{
  ""id"": ""wv-ffac14be-00ee-4a72-a08e-f5b0956171c4"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: 7eff5a2c-5d5d-4989-a68f-a1362b0dad7c >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7eff5a2c-5d5d-4989-a68f-a1362b0dad7c', 'ffac14be-00ee-4a72-a08e-f5b0956171c4', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcRow', 'body', '{
  ""visible_columns"": 4,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 3,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_self_align"": ""1"",
  ""container1_flex_order"": 0,
  ""container2_span"": 3,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_self_align"": ""1"",
  ""container2_flex_order"": 0,
  ""container3_span"": 3,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_self_align"": ""1"",
  ""container3_flex_order"": 0,
  ""container4_span"": -1,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_self_align"": ""4"",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_self_align"": ""1"",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_self_align"": ""1"",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_self_align"": ""1"",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_self_align"": ""1"",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_self_align"": ""1"",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_self_align"": ""1"",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_self_align"": ""1"",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_self_align"": ""1"",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: 2e98b41a-f845-4ab1-aeaf-944ae963883b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('2e98b41a-f845-4ab1-aeaf-944ae963883b', '7eff5a2c-5d5d-4989-a68f-a1362b0dad7c', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column4', '{
  ""type"": ""1"",
  ""text"": ""generate"",
  ""color"": ""1"",
  ""size"": ""3"",
  ""class"": ""mb-3"",
  ""id"": """",
  ""icon_class"": ""fa fa-cog"",
  ""is_block"": ""true"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: 70424cd2-2b69-4c87-9977-cb60a72239fd >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('70424cd2-2b69-4c87-9977-cb60a72239fd', '7eff5a2c-5d5d-4989-a68f-a1362b0dad7c', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column1', '{
  ""label_text"": ""Year"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SampleCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry\\n\\t\\t{\\n\\t\\t    var value = (string)pageModel.DataModel.GetProperty(\\\""RequestQuery.year\\\"");\\n\\t\\t    if(string.IsNullOrWhiteSpace(value))\\n\\t\\t\\t    return DateTime.Now.Year;\\n\\t\\t\\telse\\n\\t\\t\\t    return value;\\n\\t\\t}\\n\\t\\tcatch(PropertyDoesNotExistException ex)\\n\\t\\t{\\n\\t\\t  return DateTime.Now.Year;\\n\\t\\t}\\n\\t\\tcatch(Exception ex)\\n\\t\\t{\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\"",\""default\"":\""\""}"",
  ""name"": ""year"",
  ""mode"": ""0"",
  ""decimal_digits"": 0,
  ""min"": 2000,
  ""max"": 2100,
  ""step"": 1,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: 0c32036a-4432-4b17-beb7-198ba22ea134 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0c32036a-4432-4b17-beb7-198ba22ea134', '7eff5a2c-5d5d-4989-a68f-a1362b0dad7c', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column2', '{
  ""label_text"": ""Month"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SampleCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry\\n\\t\\t{\\n\\t\\t    var value = (string)pageModel.DataModel.GetProperty(\\\""RequestQuery.month\\\"");\\n\\t\\t    if(string.IsNullOrWhiteSpace(value))\\n\\t\\t\\t    return DateTime.Now.Month;\\n\\t\\t\\telse\\n\\t\\t\\t    return value;\\n\\t\\t}\\n\\t\\tcatch(PropertyDoesNotExistException ex)\\n\\t\\t{\\n\\t\\t  return DateTime.Now.Month;\\n\\t\\t}\\n\\t\\tcatch(Exception ex)\\n\\t\\t{\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\"",\""default\"":\""\""}"",
  ""name"": ""month"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""MonthSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""0"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: account-monthly-timelog  id: bfd0dba8-dc50-4881-9815-7f5e56a6a2fb >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bfd0dba8-dc50-4881-9815-7f5e56a6a2fb', '7eff5a2c-5d5d-4989-a68f-a1362b0dad7c', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column3', '{
  ""label_text"": ""Account"",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SampleCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\ttry\\n\\t\\t{\\n\\t\\t    return pageModel.DataModel.GetProperty(\\\""RequestQuery.account\\\"");\\n\\t\\t}\\n\\t\\tcatch(Exception ex){\\n\\t\\t\\treturn \\\""Error: \\\"" + ex.Message;\\n\\t\\t}\\n\\t}\\n}\"",\""default\"":\""\""}"",
  ""name"": ""account"",
  ""try_connect_to_entity"": ""false"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AccountSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""0""
}');");

            // << ***Create page body node*** Page name: list  id: d4c56ca4-52f8-47b8-8d62-e5a43930b377 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d4c56ca4-52f8-47b8-8d62-e5a43930b377', NULL, '84b892fc-6ca4-4c7e-8b7c-2f2f6954862f', NULL, 2, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 3,
  ""class"": """",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_selft_align"": """",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_selft_align"": """",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_selft_align"": """",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_selft_align"": """",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_selft_align"": """",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_selft_align"": """",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_selft_align"": """",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_selft_align"": """",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_selft_align"": """",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_selft_align"": """",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_selft_align"": """",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_selft_align"": """",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: list  id: a7720205-8f62-4319-98c3-17c6e3a0462b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a7720205-8f62-4319-98c3-17c6e3a0462b', 'd4c56ca4-52f8-47b8-8d62-e5a43930b377', '84b892fc-6ca4-4c7e-8b7c-2f2f6954862f', NULL, 1, 'WebVella.Erp.Web.Components.PcHtmlBlock', 'column1', '{
  ""html"": ""{\""type\"":\""2\"",\""string\"":\""<div class=\\\""card app-card shadow-sm mb-3 shadow-hover\\\"">\\n\\t<div class=\\\""card-body p-0\\\"">\\n\\t    <div class=\\\""row no-gutters\\\"">\\n\\t\\t\\t<div class=\\\""col-lg-3\\\"">\\n\\t\\t\\t\\t<div class=\\\""app-image-wrapper pt-5 pb-5 pt-lg-0 pb-lg-0 go-bkg-blue-light\\\"">\\n\\t\\t\\t\\t\\t<span class=\\\""app-icon fa fa-database go-blue\\\""></span>\\n\\t\\t\\t\\t</div>\\n\\t\\t\\t</div>\\n\\t\\t\\t<div class=\\\""col-lg-9\\\"">\\n        \\t\\t<div class=\\\""app-meta p-3 p-lg-3\\\"">\\n        \\t\\t\\t<h3 class=\\\""label\\\"">Monthly timelog for an account</h3>\\n        \\t\\t\\t<div class=\\\""description mb-0\\\"">Lists all tasks that were worked on for the selected month and account, their billable and nonbillable total for the period</div>\\n        \\t\\t</div>\\n\\t\\t\\t</div>\\n\\t\\t</div>\\n\\n\\t</div>\\n    <a class=\\\""app-link\\\"" href=\\\""/projects/reports/list/a/account-monthly-timelog\\\""><em></em></a>\\n</div>\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: feed  id: 3f85bfe4-5040-42c6-a3fb-fefc9ab59b10 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('3f85bfe4-5040-42c6-a3fb-fefc9ab59b10', NULL, 'dfe56667-174d-492d-8f84-b8ab8b70c63f', NULL, 3, 'WebVella.Erp.Plugins.Project.Components.PcFeedList', '', '{
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""FeedItemsForRecordId\"",\""default\"":\""\""}""
}');");

            // << ***Create page body node*** Page name: open  id: 250115da-cea5-46f3-a77a-d2f7704c650d >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('250115da-cea5-46f3-a77a-d2f7704c650d', NULL, '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcPageHeader', '', '{
  ""area_label"": ""{\""type\"":\""0\"",\""string\"":\""App.Label\"",\""default\"":\""\""}"",
  ""area_sublabel"": """",
  ""title"": ""{\""type\"":\""0\"",\""string\"":\""Page.Label\"",\""default\"":\""\""}"",
  ""subtitle"": """",
  ""description"": """",
  ""show_page_switch"": ""true"",
  ""color"": ""{\""type\"":\""0\"",\""string\"":\""Entity.Color\"",\""default\"":\""\""}"",
  ""icon_color"": ""#fff"",
  ""icon_class"": ""{\""type\"":\""0\"",\""string\"":\""Entity.IconName\"",\""default\"":\""\""}"",
  ""return_url"": """"
}');");

            // << ***Create page body node*** Page name: open  id: e1b676c0-e128-46a2-b2cc-51a5b3ec2816 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e1b676c0-e128-46a2-b2cc-51a5b3ec2816', '250115da-cea5-46f3-a77a-d2f7704c650d', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'actions', '{
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
  ""onclick"": ""ErpEvent.DISPATCH(''WebVella.Erp.Web.Components.PcDrawer'',''open'')"",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: all  id: 8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', NULL, '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 8,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""AllTasks\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No records"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_class"": """",
  ""container1_vertical_align"": ""1"",
  ""container1_horizontal_align"": ""1"",
  ""container2_label"": ""type"",
  ""container2_width"": ""20px"",
  ""container2_name"": ""type"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_class"": """",
  ""container2_vertical_align"": ""1"",
  ""container2_horizontal_align"": ""1"",
  ""container3_label"": ""key"",
  ""container3_width"": ""120px"",
  ""container3_name"": ""key"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_class"": """",
  ""container3_vertical_align"": ""1"",
  ""container3_horizontal_align"": ""1"",
  ""container4_label"": ""task"",
  ""container4_width"": """",
  ""container4_name"": ""task"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_class"": """",
  ""container4_vertical_align"": ""1"",
  ""container4_horizontal_align"": ""1"",
  ""container5_label"": ""owner"",
  ""container5_width"": ""120px"",
  ""container5_name"": ""owner_id"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""true"",
  ""container5_class"": """",
  ""container5_vertical_align"": ""1"",
  ""container5_horizontal_align"": ""1"",
  ""container6_label"": ""created by"",
  ""container6_width"": ""120px"",
  ""container6_name"": ""created_by"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""true"",
  ""container6_class"": """",
  ""container6_vertical_align"": ""1"",
  ""container6_horizontal_align"": ""1"",
  ""container7_label"": ""target date"",
  ""container7_width"": ""120px"",
  ""container7_name"": ""target_date"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_class"": """",
  ""container7_vertical_align"": ""1"",
  ""container7_horizontal_align"": ""1"",
  ""container8_label"": ""status"",
  ""container8_width"": ""120px"",
  ""container8_name"": ""status"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_class"": """",
  ""container8_vertical_align"": ""1"",
  ""container8_horizontal_align"": ""1"",
  ""container9_label"": """",
  ""container9_width"": """",
  ""container9_name"": """",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_class"": """",
  ""container9_vertical_align"": ""1"",
  ""container9_horizontal_align"": ""1"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_class"": """",
  ""container10_vertical_align"": ""1"",
  ""container10_horizontal_align"": ""1"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_class"": """",
  ""container11_vertical_align"": ""1"",
  ""container11_horizontal_align"": ""1"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_class"": """",
  ""container12_vertical_align"": ""1"",
  ""container12_horizontal_align"": ""1""
}');");

            // << ***Create page body node*** Page name: all  id: 15df96da-8d77-427f-a2a1-23017a6f8800 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('15df96da-8d77-427f-a2a1-23017a6f8800', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column8', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$task_status_1n_task[0].label\"",\""default\"":\""n/a\""}"",
  ""name"": ""status_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: 9bdd70b0-aa1d-4458-ad95-9fc455236350 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9bdd70b0-aa1d-4458-ad95-9fc455236350', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column3', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.key\"",\""default\"":\""key\""}"",
  ""name"": ""key"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: 08d8dad6-594d-498f-aa0b-555d245ce9e2 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('08d8dad6-594d-498f-aa0b-555d245ce9e2', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column5', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: e5756351-b9c2-4bd9-bcbc-be3cc9fb3751 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('e5756351-b9c2-4bd9-bcbc-be3cc9fb3751', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar typeRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord.$task_type_1n_task[0]\\\"");\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (typeRecord == null)\\n\\t\\t\\treturn null;\\n\\n        var iconClass=\\\""fa fa-fw fa-file\\\"";\\n        var color=\\\""#999\\\"";\\n        if(typeRecord[\\\""icon_class\\\""] != null){\\n            iconClass = (string)typeRecord[\\\""icon_class\\\""];\\n        }\\n        if(typeRecord[\\\""color\\\""] != null){\\n            color = (string)typeRecord[\\\""color\\\""];\\n        }\\n\\t\\treturn $\\\""<i class=\\\\\\\""{iconClass}\\\\\\\"" style=\\\\\\\""color:{color};font-size:23px;\\\\\\\"" title=\\\\\\\""{typeRecord[\\\""label\\\""]}\\\\\\\""></i>\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""icon\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: all  id: ad9c357f-e620-4ed1-9593-d76c97019677 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ad9c357f-e620-4ed1-9593-d76c97019677', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column7', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.target_date\"",\""default\"":\""\""}"",
  ""name"": ""target_date"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4""
}');");

            // << ***Create page body node*** Page name: all  id: 0660124a-cbf0-47ff-9757-3f072c39953a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0660124a-cbf0-47ff-9757-3f072c39953a', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column6', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task_creator[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: 5899b892-ee3d-4cb9-9811-a24ff8f1b791 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('5899b892-ee3d-4cb9-9811-a24ff8f1b791', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.subject\"",\""default\"":\""Task subject\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: all  id: a918fec1-865b-4c54-8f93-685ffe85fb90 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a918fec1-865b-4c54-8f93-685ffe85fb90', '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//replace constants with your values\\n\\t\\tconst string DATASOURCE_NAME = \\\""RowRecord.id\\\"";\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<Guid>(DATASOURCE_NAME);\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (dataSource == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\treturn $\\\""/projects/tasks/tasks/r/{dataSource}/details?returnUrl=/projects/tasks/tasks/l/all\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: 73d24cb2-ae13-4ddd-9ea8-80d8ef6c2911 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('73d24cb2-ae13-4ddd-9ea8-80d8ef6c2911', NULL, 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 3, 'WebVella.Erp.Web.Components.PcDrawer', '', '{
  ""title"": ""Search Tasks"",
  ""width"": ""550px"",
  ""class"": """",
  ""body_class"": """",
  ""title_action_html"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: d1580be1-733d-477e-bd4a-65e325a8a263 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d1580be1-733d-477e-bd4a-65e325a8a263', '73d24cb2-ae13-4ddd-9ea8-80d8ef6c2911', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-156877b1-d1ea-4fea-be4a-62a982bef3a7"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: no-owner  id: 9888344d-c88f-4d1a-9984-7a718779e4cc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9888344d-c88f-4d1a-9984-7a718779e4cc', 'd1580be1-733d-477e-bd4a-65e325a8a263', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcGridFilterField', 'body', '{
  ""label"": ""Task contents"",
  ""name"": ""x_search"",
  ""try_connect_to_entity"": ""true"",
  ""field_type"": ""18"",
  ""query_type"": ""2"",
  ""query_options"": [
    ""2""
  ],
  ""prefix"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: c150a0fa-9c1a-4f05-a842-22d374e2c2e6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('c150a0fa-9c1a-4f05-a842-22d374e2c2e6', 'd1580be1-733d-477e-bd4a-65e325a8a263', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""1"",
  ""text"": ""Search Tasks"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: feed  id: ff5b4808-9c2a-4d4f-8eaf-a4878594c55a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ff5b4808-9c2a-4d4f-8eaf-a4878594c55a', NULL, 'acb76466-32b8-428c-81cb-47b6013879e7', NULL, 3, 'WebVella.Erp.Web.Components.PcFieldHtml', '', '{
  ""label_text"": """",
  ""label_mode"": ""0"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n        try{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar queryRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RequestQuery\\\"");\\n        var type = \\\""\\\"";\\n        if(queryRecord.Properties.ContainsKey(\\\""type\\\""))\\n            type = (string)queryRecord[\\\""type\\\""];\\n\\n        var result = \\\""\\\"";\\n        result += $\\\""<ul class=\\\\\\\""nav nav-pills nav-sm mb-4\\\\\\\"">\\\"";\\n        result += $\\\""<li class=\\\\\\\""nav-item\\\\\\\"">\\\"";\\n        result += $\\\""<a class=\\\\\\\""nav-link {(type == \\\""\\\"" ? \\\""active\\\"" : \\\""\\\"")}\\\\\\\"" href=\\\\\\\""/projects/feed/feed/a/feed\\\\\\\"">All Feeds</a>\\\"";\\n\\t    result += $\\\""</li>\\\"";\\n\\t\\tresult += $\\\""<li class=\\\\\\\""nav-item\\\\\\\"">\\\"";\\n\\t\\tresult += $\\\""<a class=\\\\\\\""nav-link  {(type == \\\""task\\\"" ? \\\""active\\\"" : \\\""\\\"")}\\\\\\\"" href=\\\\\\\""/projects/feed/feed/a/feed?type=task\\\\\\\"">Task</a>\\\"";\\n\\t    result += $\\\""</li>\\\"";\\n\\t\\tresult += $\\\""<li class=\\\\\\\""nav-item\\\\\\\"">\\\"";\\n\\t\\tresult += $\\\""<a class=\\\\\\\""nav-link  {(type == \\\""comment\\\"" ? \\\""active\\\"" : \\\""\\\"")}\\\\\\\"" href=\\\\\\\""/projects/feed/feed/a/feed?type=comment\\\\\\\"">Comment</a>\\\"";\\n\\t    result += $\\\""</li>\\\"";\\n\\t\\tresult += $\\\""<li class=\\\\\\\""nav-item\\\\\\\"">\\\"";\\n\\t\\tresult += $\\\""<a class=\\\\\\\""nav-link  {(type == \\\""timelog\\\"" ? \\\""active\\\"" : \\\""\\\"")}\\\\\\\"" href=\\\\\\\""/projects/feed/feed/a/feed?type=timelog\\\\\\\"">Timelog</a>\\\"";\\n\\t    result += $\\\""</li>\\\"";\\t    \\n        result += $\\\""</ul>\\\"";\\t    \\n\\t\\treturn result;\\n        }\\n        catch(Exception ex){\\n            return ex.Message;\\n        }\\n\\t}\\n}\\n\"",\""default\"":\""Feed type Pill navigation\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: details  id: 6de13934-ca81-4807-bb71-cadcdbb99ca7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('6de13934-ca81-4807-bb71-cadcdbb99ca7', NULL, '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 4, 'WebVella.Erp.Web.Components.PcRow', '', '{
  ""visible_columns"": 3,
  ""class"": ""mt-4"",
  ""no_gutters"": ""false"",
  ""flex_vertical_alignment"": ""1"",
  ""flex_horizontal_alignment"": ""1"",
  ""container1_span"": 0,
  ""container1_span_sm"": 0,
  ""container1_span_md"": 0,
  ""container1_span_lg"": 0,
  ""container1_span_xl"": 0,
  ""container1_offset"": 0,
  ""container1_offset_sm"": 0,
  ""container1_offset_md"": 0,
  ""container1_offset_lg"": 0,
  ""container1_offset_xl"": 0,
  ""container1_flex_self_align"": ""1"",
  ""container1_flex_order"": 0,
  ""container2_span"": 0,
  ""container2_span_sm"": 0,
  ""container2_span_md"": 0,
  ""container2_span_lg"": 0,
  ""container2_span_xl"": 0,
  ""container2_offset"": 0,
  ""container2_offset_sm"": 0,
  ""container2_offset_md"": 0,
  ""container2_offset_lg"": 0,
  ""container2_offset_xl"": 0,
  ""container2_flex_self_align"": ""1"",
  ""container2_flex_order"": 0,
  ""container3_span"": 0,
  ""container3_span_sm"": 0,
  ""container3_span_md"": 0,
  ""container3_span_lg"": 0,
  ""container3_span_xl"": 0,
  ""container3_offset"": 0,
  ""container3_offset_sm"": 0,
  ""container3_offset_md"": 0,
  ""container3_offset_lg"": 0,
  ""container3_offset_xl"": 0,
  ""container3_flex_self_align"": ""1"",
  ""container3_flex_order"": 0,
  ""container4_span"": 0,
  ""container4_span_sm"": 0,
  ""container4_span_md"": 0,
  ""container4_span_lg"": 0,
  ""container4_span_xl"": 0,
  ""container4_offset"": 0,
  ""container4_offset_sm"": 0,
  ""container4_offset_md"": 0,
  ""container4_offset_lg"": 0,
  ""container4_offset_xl"": 0,
  ""container4_flex_self_align"": ""1"",
  ""container4_flex_order"": 0,
  ""container5_span"": 0,
  ""container5_span_sm"": 0,
  ""container5_span_md"": 0,
  ""container5_span_lg"": 0,
  ""container5_span_xl"": 0,
  ""container5_offset"": 0,
  ""container5_offset_sm"": 0,
  ""container5_offset_md"": 0,
  ""container5_offset_lg"": 0,
  ""container5_offset_xl"": 0,
  ""container5_flex_self_align"": ""1"",
  ""container5_flex_order"": 0,
  ""container6_span"": 0,
  ""container6_span_sm"": 0,
  ""container6_span_md"": 0,
  ""container6_span_lg"": 0,
  ""container6_span_xl"": 0,
  ""container6_offset"": 0,
  ""container6_offset_sm"": 0,
  ""container6_offset_md"": 0,
  ""container6_offset_lg"": 0,
  ""container6_offset_xl"": 0,
  ""container6_flex_self_align"": ""1"",
  ""container6_flex_order"": 0,
  ""container7_span"": 0,
  ""container7_span_sm"": 0,
  ""container7_span_md"": 0,
  ""container7_span_lg"": 0,
  ""container7_span_xl"": 0,
  ""container7_offset"": 0,
  ""container7_offset_sm"": 0,
  ""container7_offset_md"": 0,
  ""container7_offset_lg"": 0,
  ""container7_offset_xl"": 0,
  ""container7_flex_self_align"": ""1"",
  ""container7_flex_order"": 0,
  ""container8_span"": 0,
  ""container8_span_sm"": 0,
  ""container8_span_md"": 0,
  ""container8_span_lg"": 0,
  ""container8_span_xl"": 0,
  ""container8_offset"": 0,
  ""container8_offset_sm"": 0,
  ""container8_offset_md"": 0,
  ""container8_offset_lg"": 0,
  ""container8_offset_xl"": 0,
  ""container8_flex_self_align"": ""1"",
  ""container8_flex_order"": 0,
  ""container9_span"": 0,
  ""container9_span_sm"": 0,
  ""container9_span_md"": 0,
  ""container9_span_lg"": 0,
  ""container9_span_xl"": 0,
  ""container9_offset"": 0,
  ""container9_offset_sm"": 0,
  ""container9_offset_md"": 0,
  ""container9_offset_lg"": 0,
  ""container9_offset_xl"": 0,
  ""container9_flex_self_align"": ""1"",
  ""container9_flex_order"": 0,
  ""container10_span"": 0,
  ""container10_span_sm"": 0,
  ""container10_span_md"": 0,
  ""container10_span_lg"": 0,
  ""container10_span_xl"": 0,
  ""container10_offset"": 0,
  ""container10_offset_sm"": 0,
  ""container10_offset_md"": 0,
  ""container10_offset_lg"": 0,
  ""container10_offset_xl"": 0,
  ""container10_flex_self_align"": ""1"",
  ""container10_flex_order"": 0,
  ""container11_span"": 0,
  ""container11_span_sm"": 0,
  ""container11_span_md"": 0,
  ""container11_span_lg"": 0,
  ""container11_span_xl"": 0,
  ""container11_offset"": 0,
  ""container11_offset_sm"": 0,
  ""container11_offset_md"": 0,
  ""container11_offset_lg"": 0,
  ""container11_offset_xl"": 0,
  ""container11_flex_self_align"": ""1"",
  ""container11_flex_order"": 0,
  ""container12_span"": 0,
  ""container12_span_sm"": 0,
  ""container12_span_md"": 0,
  ""container12_span_lg"": 0,
  ""container12_span_xl"": 0,
  ""container12_offset"": 0,
  ""container12_offset_sm"": 0,
  ""container12_offset_md"": 0,
  ""container12_offset_lg"": 0,
  ""container12_offset_xl"": 0,
  ""container12_flex_self_align"": ""1"",
  ""container12_flex_order"": 0
}');");

            // << ***Create page body node*** Page name: details  id: 551483ab-262b-4541-b0dc-fadaa8de5284 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('551483ab-262b-4541-b0dc-fadaa8de5284', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldDate', 'column2', '{
  ""label_text"": ""End date"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.end_date\"",\""default\"":\""\""}"",
  ""name"": ""end_date"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 7a7fbcd5-fb6f-40fd-a0cd-1a7c26e1c4ab >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('7a7fbcd5-fb6f-40fd-a0cd-1a7c26e1c4ab', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column1', '{
  ""label_text"": ""Project lead"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.owner_id\"",\""default\"":\""\""}"",
  ""name"": ""owner_id"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllUsersSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: b37c63a7-84ea-4673-9a81-ec4313c178b7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b37c63a7-84ea-4673-9a81-ec4313c178b7', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column1', '{
  ""label_text"": ""Account"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.account_id\"",\""default\"":\""\""}"",
  ""name"": ""account_id"",
  ""options"": ""{\""type\"":\""0\"",\""string\"":\""AllAccountsSelectOptions\"",\""default\"":\""\""}"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 30676929-f280-414d-8f4c-d41f851136ce >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('30676929-f280-414d-8f4c-d41f851136ce', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldSelect', 'column3', '{
  ""label_text"": ""Budget type"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.budget_type\"",\""default\"":\""\""}"",
  ""name"": ""budget_type"",
  ""options"": """",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: b2caeb51-b6a5-4e15-a317-9825511792c6 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b2caeb51-b6a5-4e15-a317-9825511792c6', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 2, 'WebVella.Erp.Web.Components.PcFieldNumber', 'column3', '{
  ""label_text"": ""Budget amount"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.budget_amount\"",\""default\"":\""\""}"",
  ""name"": ""budget_amount"",
  ""mode"": ""3"",
  ""decimal_digits"": 2,
  ""min"": 0,
  ""max"": 0,
  ""step"": 0,
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: details  id: 8a75b1d8-8184-40ed-a977-26616239fbb7 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8a75b1d8-8184-40ed-a977-26616239fbb7', '6de13934-ca81-4807-bb71-cadcdbb99ca7', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column2', '{
  ""label_text"": ""Start date"",
  ""label_mode"": ""2"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""Record.start_date\"",\""default\"":\""\""}"",
  ""name"": ""start_date"",
  ""mode"": ""3"",
  ""connected_entity_id"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: 34916453-4d5a-40a7-b74c-3c4e8b5a8950 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('34916453-4d5a-40a7-b74c-3c4e8b5a8950', NULL, 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 8,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""NoOwnerTasks\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No records"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_class"": """",
  ""container1_vertical_align"": ""1"",
  ""container1_horizontal_align"": ""1"",
  ""container2_label"": ""type"",
  ""container2_width"": ""20px"",
  ""container2_name"": ""type"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_class"": """",
  ""container2_vertical_align"": ""1"",
  ""container2_horizontal_align"": ""1"",
  ""container3_label"": ""key"",
  ""container3_width"": ""120px"",
  ""container3_name"": ""key"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_class"": """",
  ""container3_vertical_align"": ""1"",
  ""container3_horizontal_align"": ""1"",
  ""container4_label"": ""task"",
  ""container4_width"": """",
  ""container4_name"": ""task"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_class"": """",
  ""container4_vertical_align"": ""1"",
  ""container4_horizontal_align"": ""1"",
  ""container5_label"": ""owner"",
  ""container5_width"": ""120px"",
  ""container5_name"": ""owner_id"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_class"": """",
  ""container5_vertical_align"": ""1"",
  ""container5_horizontal_align"": ""1"",
  ""container6_label"": ""created by"",
  ""container6_width"": ""120px"",
  ""container6_name"": ""created_by"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_class"": """",
  ""container6_vertical_align"": ""1"",
  ""container6_horizontal_align"": ""1"",
  ""container7_label"": ""target date"",
  ""container7_width"": ""120px"",
  ""container7_name"": ""target_date"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_class"": """",
  ""container7_vertical_align"": ""1"",
  ""container7_horizontal_align"": ""1"",
  ""container8_label"": ""status"",
  ""container8_width"": ""80px"",
  ""container8_name"": ""status"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_class"": """",
  ""container8_vertical_align"": ""1"",
  ""container8_horizontal_align"": ""1"",
  ""container9_label"": """",
  ""container9_width"": """",
  ""container9_name"": """",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_class"": """",
  ""container9_vertical_align"": ""1"",
  ""container9_horizontal_align"": ""1"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_class"": """",
  ""container10_vertical_align"": ""1"",
  ""container10_horizontal_align"": ""1"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_class"": """",
  ""container11_vertical_align"": ""1"",
  ""container11_horizontal_align"": ""1"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_class"": """",
  ""container12_vertical_align"": ""1"",
  ""container12_horizontal_align"": ""1""
}');");

            // << ***Create page body node*** Page name: no-owner  id: 91bbd374-13d5-4a86-8a07-84349ec57682 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('91bbd374-13d5-4a86-8a07-84349ec57682', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column8', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$task_status_1n_task[0].label\"",\""default\"":\""n/a\""}"",
  ""name"": ""status_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: no-owner  id: d0addd75-c216-4f25-9f61-44fb29f7f160 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d0addd75-c216-4f25-9f61-44fb29f7f160', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column6', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task_creator[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: no-owner  id: 001a3188-1d23-4f85-90f2-9053eac93bbc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('001a3188-1d23-4f85-90f2-9053eac93bbc', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.subject\"",\""default\"":\""Task subject\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: no-owner  id: fd73e317-ae0a-4c54-9bed-55f9c89965a9 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fd73e317-ae0a-4c54-9bed-55f9c89965a9', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//replace constants with your values\\n\\t\\tconst string DATASOURCE_NAME = \\\""RowRecord.id\\\"";\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<Guid>(DATASOURCE_NAME);\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (dataSource == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\treturn $\\\""/projects/tasks/tasks/r/{dataSource}/details?returnUrl=/projects/tasks/tasks/l/all\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: no-owner  id: 0ba5ed3f-625e-4df3-84ab-70f064b9905a >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0ba5ed3f-625e-4df3-84ab-70f064b9905a', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column3', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.key\"",\""default\"":\""key\""}"",
  ""name"": ""key"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: no-owner  id: 0867158f-f0c2-4284-838a-1c4ec3acb796 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('0867158f-f0c2-4284-838a-1c4ec3acb796', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column5', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: no-owner  id: b2b5e677-341a-43af-9baa-0aba98a7d8c3 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('b2b5e677-341a-43af-9baa-0aba98a7d8c3', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar typeRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord.$task_type_1n_task[0]\\\"");\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (typeRecord == null)\\n\\t\\t\\treturn null;\\n\\n        var iconClass=\\\""fa fa-fw fa-file\\\"";\\n        var color=\\\""#999\\\"";\\n        if(typeRecord[\\\""icon_class\\\""] != null){\\n            iconClass = (string)typeRecord[\\\""icon_class\\\""];\\n        }\\n        if(typeRecord[\\\""color\\\""] != null){\\n            color = (string)typeRecord[\\\""color\\\""];\\n        }\\n\\t\\treturn $\\\""<i class=\\\\\\\""{iconClass}\\\\\\\"" style=\\\\\\\""color:{color};font-size:23px;\\\\\\\"" title=\\\\\\\""{typeRecord[\\\""label\\\""]}\\\\\\\""></i>\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""icon\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: no-owner  id: 8d244aa4-ad6b-464f-87e9-37a55fd18d19 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8d244aa4-ad6b-464f-87e9-37a55fd18d19', '34916453-4d5a-40a7-b74c-3c4e8b5a8950', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column7', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.target_date\"",\""default\"":\""\""}"",
  ""name"": ""target_date"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4""
}');");

            // << ***Create page body node*** Page name: open  id: 8012f7aa-e60b-4db9-a380-374c9238c12b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('8012f7aa-e60b-4db9-a380-374c9238c12b', NULL, '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 3, 'WebVella.Erp.Web.Components.PcDrawer', '', '{
  ""title"": ""Search Tasks"",
  ""width"": ""550px"",
  ""class"": """",
  ""body_class"": """",
  ""title_action_html"": """"
}');");

            // << ***Create page body node*** Page name: open  id: 9d6caedb-f43a-4ccb-a747-4c8917a6471e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9d6caedb-f43a-4ccb-a747-4c8917a6471e', '8012f7aa-e60b-4db9-a380-374c9238c12b', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcForm', 'body', '{
  ""id"": ""wv-156877b1-d1ea-4fea-be4a-62a982bef3a7"",
  ""name"": ""form"",
  ""hook_key"": """",
  ""method"": ""get"",
  ""label_mode"": ""1"",
  ""mode"": ""1""
}');");

            // << ***Create page body node*** Page name: open  id: 9fa77e2f-c21d-48ef-82ea-825eaa697412 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('9fa77e2f-c21d-48ef-82ea-825eaa697412', '9d6caedb-f43a-4ccb-a747-4c8917a6471e', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcGridFilterField', 'body', '{
  ""label"": ""Task contents"",
  ""name"": ""x_search"",
  ""try_connect_to_entity"": ""true"",
  ""field_type"": ""18"",
  ""query_type"": ""2"",
  ""query_options"": [
    ""2""
  ],
  ""prefix"": """"
}');");

            // << ***Create page body node*** Page name: open  id: ecd7a737-fc1a-4766-9ea4-81ef60f099aa >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('ecd7a737-fc1a-4766-9ea4-81ef60f099aa', '9d6caedb-f43a-4ccb-a747-4c8917a6471e', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 2, 'WebVella.Erp.Web.Components.PcButton', 'body', '{
  ""type"": ""1"",
  ""text"": ""Search Tasks"",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": """",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": """",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: open  id: a4719fbd-b3d0-4f81-b302-96f5620e17cc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a4719fbd-b3d0-4f81-b302-96f5620e17cc', NULL, '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 2, 'WebVella.Erp.Web.Components.PcGrid', '', '{
  ""visible_columns"": 8,
  ""records"": ""{\""type\"":\""0\"",\""string\"":\""AllOpenTasks\"",\""default\"":\""\""}"",
  ""id"": """",
  ""name"": """",
  ""prefix"": """",
  ""class"": """",
  ""striped"": ""false"",
  ""small"": ""true"",
  ""bordered"": ""true"",
  ""borderless"": ""false"",
  ""hover"": ""true"",
  ""responsive_breakpoint"": ""0"",
  ""empty_text"": ""No records"",
  ""has_thead"": ""true"",
  ""has_tfoot"": ""true"",
  ""container1_label"": """",
  ""container1_width"": ""40px"",
  ""container1_name"": """",
  ""container1_nowrap"": ""false"",
  ""container1_sortable"": ""false"",
  ""container1_class"": """",
  ""container1_vertical_align"": ""1"",
  ""container1_horizontal_align"": ""1"",
  ""container2_label"": ""type"",
  ""container2_width"": ""20px"",
  ""container2_name"": ""type"",
  ""container2_nowrap"": ""false"",
  ""container2_sortable"": ""false"",
  ""container2_class"": """",
  ""container2_vertical_align"": ""1"",
  ""container2_horizontal_align"": ""1"",
  ""container3_label"": ""key"",
  ""container3_width"": ""120px"",
  ""container3_name"": ""key"",
  ""container3_nowrap"": ""false"",
  ""container3_sortable"": ""false"",
  ""container3_class"": """",
  ""container3_vertical_align"": ""1"",
  ""container3_horizontal_align"": ""1"",
  ""container4_label"": ""task"",
  ""container4_width"": """",
  ""container4_name"": ""task"",
  ""container4_nowrap"": ""false"",
  ""container4_sortable"": ""false"",
  ""container4_class"": """",
  ""container4_vertical_align"": ""1"",
  ""container4_horizontal_align"": ""1"",
  ""container5_label"": ""owner"",
  ""container5_width"": ""120px"",
  ""container5_name"": ""owner_id"",
  ""container5_nowrap"": ""false"",
  ""container5_sortable"": ""false"",
  ""container5_class"": """",
  ""container5_vertical_align"": ""1"",
  ""container5_horizontal_align"": ""1"",
  ""container6_label"": ""created by"",
  ""container6_width"": ""120px"",
  ""container6_name"": ""created_by"",
  ""container6_nowrap"": ""false"",
  ""container6_sortable"": ""false"",
  ""container6_class"": """",
  ""container6_vertical_align"": ""1"",
  ""container6_horizontal_align"": ""1"",
  ""container7_label"": ""target date"",
  ""container7_width"": ""120px"",
  ""container7_name"": ""target_date"",
  ""container7_nowrap"": ""false"",
  ""container7_sortable"": ""false"",
  ""container7_class"": """",
  ""container7_vertical_align"": ""1"",
  ""container7_horizontal_align"": ""1"",
  ""container8_label"": ""status"",
  ""container8_width"": ""80px"",
  ""container8_name"": ""status"",
  ""container8_nowrap"": ""false"",
  ""container8_sortable"": ""false"",
  ""container8_class"": """",
  ""container8_vertical_align"": ""1"",
  ""container8_horizontal_align"": ""1"",
  ""container9_label"": """",
  ""container9_width"": """",
  ""container9_name"": """",
  ""container9_nowrap"": ""false"",
  ""container9_sortable"": ""false"",
  ""container9_class"": """",
  ""container9_vertical_align"": ""1"",
  ""container9_horizontal_align"": ""1"",
  ""container10_label"": ""column10"",
  ""container10_width"": """",
  ""container10_name"": ""column10"",
  ""container10_nowrap"": ""false"",
  ""container10_sortable"": ""false"",
  ""container10_class"": """",
  ""container10_vertical_align"": ""1"",
  ""container10_horizontal_align"": ""1"",
  ""container11_label"": ""column11"",
  ""container11_width"": """",
  ""container11_name"": ""column11"",
  ""container11_nowrap"": ""false"",
  ""container11_sortable"": ""false"",
  ""container11_class"": """",
  ""container11_vertical_align"": ""1"",
  ""container11_horizontal_align"": ""1"",
  ""container12_label"": ""column12"",
  ""container12_width"": """",
  ""container12_name"": ""column12"",
  ""container12_nowrap"": ""false"",
  ""container12_sortable"": ""false"",
  ""container12_class"": """",
  ""container12_vertical_align"": ""1"",
  ""container12_horizontal_align"": ""1""
}');");

            // << ***Create page body node*** Page name: open  id: a38a8496-5f14-4c23-bd04-f036c4629824 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('a38a8496-5f14-4c23-bd04-f036c4629824', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column6', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task_creator[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: open  id: 2a4c131b-69b8-43bf-bf0f-7360cd953797 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('2a4c131b-69b8-43bf-bf0f-7360cd953797', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column4', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.subject\"",\""default\"":\""Task subject\""}"",
  ""name"": ""subject"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: open  id: eb2e70db-8215-4097-a0f5-bc5154f21153 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('eb2e70db-8215-4097-a0f5-bc5154f21153', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcButton', 'column1', '{
  ""type"": ""2"",
  ""text"": """",
  ""color"": ""0"",
  ""size"": ""3"",
  ""class"": """",
  ""id"": """",
  ""icon_class"": ""fa fa-eye"",
  ""is_block"": ""false"",
  ""is_outline"": ""false"",
  ""is_active"": ""false"",
  ""is_disabled"": ""false"",
  ""onclick"": """",
  ""href"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//replace constants with your values\\n\\t\\tconst string DATASOURCE_NAME = \\\""RowRecord.id\\\"";\\n\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar dataSource = pageModel.TryGetDataSourceProperty<Guid>(DATASOURCE_NAME);\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (dataSource == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\treturn $\\\""/projects/tasks/tasks/r/{dataSource}/details?returnUrl=/projects/tasks/tasks/l/all\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""\""}"",
  ""new_tab"": ""false"",
  ""form"": """"
}');");

            // << ***Create page body node*** Page name: open  id: 35cb5466-3654-426d-97cd-caa83bb5ed3e >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('35cb5466-3654-426d-97cd-caa83bb5ed3e', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldHtml', 'column2', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""1\"",\""string\"":\""using System;\\nusing System.Collections.Generic;\\nusing WebVella.Erp.Web.Models;\\nusing WebVella.Erp.Api.Models;\\n\\npublic class SelectOptionsConvertCodeVariable : ICodeVariable\\n{\\n\\tpublic object Evaluate(BaseErpPageModel pageModel)\\n\\t{\\n\\t\\t//if pageModel is not provided, returns empty List<SelectOption>()\\n\\t\\tif (pageModel == null)\\n\\t\\t\\treturn null;\\n\\n\\t\\t//try read data source by name and get result as specified type object\\n\\t\\tvar typeRecord = pageModel.TryGetDataSourceProperty<EntityRecord>(\\\""RowRecord.$task_type_1n_task[0]\\\"");\\n\\n\\t\\t//if data source not found or different type, return empty List<SelectOption>()\\n\\t\\tif (typeRecord == null)\\n\\t\\t\\treturn null;\\n\\n        var iconClass=\\\""fa fa-fw fa-file\\\"";\\n        var color=\\\""#999\\\"";\\n        if(typeRecord[\\\""icon_class\\\""] != null){\\n            iconClass = (string)typeRecord[\\\""icon_class\\\""];\\n        }\\n        if(typeRecord[\\\""color\\\""] != null){\\n            color = (string)typeRecord[\\\""color\\\""];\\n        }\\n\\t\\treturn $\\\""<i class=\\\\\\\""{iconClass}\\\\\\\"" style=\\\\\\\""color:{color};font-size:23px;\\\\\\\"" title=\\\\\\\""{typeRecord[\\\""label\\\""]}\\\\\\\""></i>\\\"";\\n\\t}\\n}\\n\"",\""default\"":\""icon\""}"",
  ""name"": ""field"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""upload_mode"": ""1"",
  ""toolbar_mode"": ""1""
}');");

            // << ***Create page body node*** Page name: open  id: bd05d5ef-0ab4-48b0-a40e-5959875d071b >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('bd05d5ef-0ab4-48b0-a40e-5959875d071b', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldDate', 'column7', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.target_date\"",\""default\"":\""\""}"",
  ""name"": ""target_date"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4""
}');");

            // << ***Create page body node*** Page name: open  id: fdf94ef9-2130-4600-a3b1-53d1cb5489fc >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('fdf94ef9-2130-4600-a3b1-53d1cb5489fc', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column3', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.key\"",\""default\"":\""key\""}"",
  ""name"": ""key"",
  ""try_connect_to_entity"": ""true"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: open  id: 87e08d83-c8fb-4a41-a371-1316b6da0b17 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('87e08d83-c8fb-4a41-a371-1316b6da0b17', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column5', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$user_1n_task[0].username\"",\""default\"":\""n/a\""}"",
  ""name"": ""owner_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");

            // << ***Create page body node*** Page name: open  id: d1b4df6b-5ce7-4831-8d59-efd2cfdd4d51 >>
            migrationBuilder.Sql(@"INSERT INTO page_body_nodes (id, parent_id, page_id, node_id, weight, component_name, container_id, options) VALUES ('d1b4df6b-5ce7-4831-8d59-efd2cfdd4d51', 'a4719fbd-b3d0-4f81-b302-96f5620e17cc', '273dd749-3804-48c8-8306-078f1e7f3b3f', NULL, 1, 'WebVella.Erp.Web.Components.PcFieldText', 'column8', '{
  ""label_text"": """",
  ""label_mode"": ""3"",
  ""value"": ""{\""type\"":\""0\"",\""string\"":\""RowRecord.$task_status_1n_task[0].label\"",\""default\"":\""n/a\""}"",
  ""name"": ""status_id"",
  ""try_connect_to_entity"": ""false"",
  ""mode"": ""4"",
  ""maxlength"": 0
}');");


            // ========================================
            // Page Data Sources
            // ========================================

            // << ***Create page data source*** Name: Accounts >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('a2db7724-f05b-4820-9269-64792398c309', '2f11031a-41da-4dfc-8e40-ddc6dca71e2c', '61d21547-b353-48b8-8b75-b727680da79e', 'Accounts', '[{""name"":""name"",""type"":""text"",""value"":""{{RequestQuery.q_name_v}}""},{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy ?? name}}""},{""name"":""page"",""type"":""int"",""value"":""{{RequestQuery.page ?? 1 }}""}]');");

            // << ***Create page data source*** Name: TaskTypes >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('d13ee96e-64e6-4174-b16d-c1c5a7bcb9f9', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '4857ace4-fcfc-4803-ad86-7c7afba91ce0', 'TaskTypes', '[]');");

            // << ***Create page data source*** Name: AllProjects >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('993d643a-1c10-4475-8b1f-3e5ac5f2e036', '57db749f-e69e-4d88-b9d1-66203da05da1', '96218f33-42f1-4ff1-926c-b1765e1f8c6e', 'AllProjects', '[{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy ?? name}}""}]');");

            // << ***Create page data source*** Name: AllUsers >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('a94b7669-edd2-484e-88fb-d480f79b4ec6', 'c2e38698-24cd-4209-b560-02c225f3ff4a', 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd', 'AllUsers', '[]');");

            // << ***Create page data source*** Name: AllAccounts >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('6e38b5c3-43ba-4d5e-8454-11e7f6eef235', 'c2e38698-24cd-4209-b560-02c225f3ff4a', '61d21547-b353-48b8-8b75-b727680da79e', 'AllAccounts', '[]');");

            // << ***Create page data source*** Name: AllUsersSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('1487e7c6-60b2-4c2c-9ebe-0648435d2330', 'c2e38698-24cd-4209-b560-02c225f3ff4a', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllUsersSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllUsers""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""username""}]');");

            // << ***Create page data source*** Name: AllAccountsSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('7d05f40e-71ae-49de-9dd0-2231b1c9265a', 'c2e38698-24cd-4209-b560-02c225f3ff4a', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllAccountsSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllAccounts""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""name""}]');");

            // << ***Create page data source*** Name: AllAccountsSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('8b29596b-3310-46e0-838b-682e243f4611', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllAccountsSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllAccounts""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""name""}]');");

            // << ***Create page data source*** Name: AllUsers >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('fefbdab5-57ee-4343-9355-199c154bde3d', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd', 'AllUsers', '[]');");

            // << ***Create page data source*** Name: AllUsersSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('f92520fe-8ea9-4284-a991-bb74810660e5', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllUsersSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllUsers""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""username""}]');");

            // << ***Create page data source*** Name: AllUsers >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('defaf774-60d6-4c15-9683-da15ca53730c', '68100014-1fd7-456c-9b26-27aa9f858287', 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd', 'AllUsers', '[]');");

            // << ***Create page data source*** Name: AllUsersSelectOption >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('ebf5c697-3a01-4759-b9c6-ec7f3414bb54', '68100014-1fd7-456c-9b26-27aa9f858287', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllUsersSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllUsers""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""username""}]');");

            // << ***Create page data source*** Name: AllProjects >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('c4bb6351-2fa9-4953-852f-62eb782e839c', '68100014-1fd7-456c-9b26-27aa9f858287', '96218f33-42f1-4ff1-926c-b1765e1f8c6e', 'AllProjects', '[]');");

            // << ***Create page data source*** Name: AllProjectsSelectOption >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('561c85b5-b016-4420-8770-9752ff5347b9', '68100014-1fd7-456c-9b26-27aa9f858287', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllProjectsSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllProjects""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""name""}]');");

            // << ***Create page data source*** Name: MonthSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('e58cc762-4e3f-4b6f-9968-f3b6ed907a86', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', 'bd83b38b-0211-4aab-9049-97e9e2847c57', 'MonthSelectOptions', '[]');");

            // << ***Create page data source*** Name: AllProjectTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('6c41cbd7-d99f-4019-84f0-24361bfd7a0a', '6f673561-fad7-4844-8262-589834f1b2ce', 'c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd', 'AllProjectTasks', '[{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy}}""},{""name"":""sortOrder"",""type"":""text"",""value"":""{{RequestQuery.sortOrder}}""},{""name"":""page"",""type"":""int"",""value"":""{{RequestQuery.page}}""},{""name"":""searchQuery"",""type"":""text"",""value"":""{{RequestQuery.q_x_search_v}}""},{""name"":""projectId"",""type"":""guid"",""value"":""{{ParentRecord.id}}""}]');");

            // << ***Create page data source*** Name: TaskAuxData >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('f8c429ee-c6fe-457d-9339-44e626a6dd27', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '587d963b-613f-4e77-a7d4-719f631ce6b2', 'TaskAuxData', '[{""name"":""recordId"",""type"":""guid"",""value"":""{{Record.id}}""}]');");

            // << ***Create page data source*** Name: AllUsers >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('5ff5cc0c-c06e-4b58-8a31-4714914778aa', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd', 'AllUsers', '[]');");

            // << ***Create page data source*** Name: AllUsersSelectOption >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('43691d9f-65ef-433c-934b-ccf6eaafdd3f', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '12dcdf08-af03-4347-8015-bd9bace17514', 'AllUsersSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllUsers""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""username""}]');");

            // << ***Create page data source*** Name: TaskTypesSelectOption >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('750213cb-8c69-4749-b10f-211b53369958', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '12dcdf08-af03-4347-8015-bd9bace17514', 'TaskTypesSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""TaskTypes""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""label""}]');");

            // << ***Create page data source*** Name: TaskStatuses >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('f09fe186-8617-4f94-a67b-3a69172b1257', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', 'fad53f3d-4d3b-4c7b-8cd2-23e96a086ad8', 'TaskStatuses', '[]');");

            // << ***Create page data source*** Name: AllTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('9c8ec6cc-b389-4baa-b4ce-770edf2520dd', '6d3fe557-59dd-4a2e-b710-f3f326ae172b', '5a6e9d56-63bc-43b1-b95e-24838db9f435', 'AllTasks', '[{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy}}""},{""name"":""sortOrder"",""type"":""text"",""value"":""{{RequestQuery.sortOrder}}""},{""name"":""page"",""type"":""int"",""value"":""{{RequestQuery.page}}""},{""name"":""searchQuery"",""type"":""text"",""value"":""{{RequestQuery.q_x_search_v}}""}]');");

            // << ***Create page data source*** Name: TaskTypes >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('9e50f76d-f56c-4204-9d8b-4db8860371a5', '68100014-1fd7-456c-9b26-27aa9f858287', '4857ace4-fcfc-4803-ad86-7c7afba91ce0', 'TaskTypes', '[]');");

            // << ***Create page data source*** Name: TaskTypeSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('120c783a-f04c-4be9-a9ef-f991aae3d648', '68100014-1fd7-456c-9b26-27aa9f858287', '12dcdf08-af03-4347-8015-bd9bace17514', 'TaskTypeSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""TaskTypes""}]');");

            // << ***Create page data source*** Name: AllAccounts >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('cf3b936e-ec45-4937-a157-a008ef97d594', '7a0aad34-0f2f-4c40-a77f-cee92c9550a3', '61d21547-b353-48b8-8b75-b727680da79e', 'AllAccounts', '[]');");

            // << ***Create page data source*** Name: FeedItemsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('ee65976e-d5d0-4dd4-ac6a-2047e8817add', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '74e5a414-6deb-4af6-8e29-567f718ca430', 'FeedItemsForRecordId', '[{""name"":""recordId"",""type"":""text"",""value"":""{{Record.id}}""}]');");

            // << ***Create page data source*** Name: CommentsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('2f523831-0437-4250-a6b5-8eeb3da9d04c', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', 'a588e096-358d-4426-adf6-5db693f32322', 'CommentsForRecordId', '[{""name"":""recordId"",""type"":""text"",""value"":""{{Record.id}}""}]');");

            // << ***Create page data source*** Name: TimeLogsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('24e093ae-ab0f-4c52-86b2-9e1fe2ed2a0a', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', 'e66b8374-82ea-4305-8456-085b3a1f1f2d', 'TimeLogsForRecordId', '[{""name"":""recordId"",""type"":""text"",""value"":""{{Record.id}}""}]');");

            // << ***Create page data source*** Name: CurrentDate >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('361dc0a8-68b8-45ec-8002-11779a304899', '33f2cd33-cf38-4247-9097-75f895d1ef7a', '64207638-d75e-4a25-9965-6e35b0aa835a', 'CurrentDate', '[]');");

            // << ***Create page data source*** Name: ProjectWidgetMyTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('e688cbdd-0fa9-43b4-aed1-3d667fdecf87', '33f2cd33-cf38-4247-9097-75f895d1ef7a', 'c44eab77-c81e-4f55-95c8-4949b275fc99', 'ProjectWidgetMyTasks', '[{""name"":""userId"",""type"":""guid"",""value"":""{{CurrentUser.Id}}""},{""name"":""currentDate"",""type"":""date"",""value"":""{{CurrentDate}}""}]');");

            // << ***Create page data source*** Name: TaskStatusesSelectOption >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('f5a2f77f-6d79-4180-b73f-7deb21895f4e', '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c', '12dcdf08-af03-4347-8015-bd9bace17514', 'TaskStatusesSelectOption', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""TaskStatuses""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""label""},{""name"":""SortOrderPropName"",""type"":""text"",""value"":""sort_index""}]');");

            // << ***Create page data source*** Name: FeedItemsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('7717b418-7eed-472a-b4cd-6ada2e85d6df', 'dfe56667-174d-492d-8f84-b8ab8b70c63f', '74e5a414-6deb-4af6-8e29-567f718ca430', 'FeedItemsForRecordId', '[{""name"":""recordId"",""type"":""text"",""value"":""{{Record.id}}""},{""name"":""type"",""type"":""text"",""value"":""{{RequestQuery.type}}""}]');");

            // << ***Create page data source*** Name: ProjectWidgetMyTasksDueToday >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('f1da592e-d696-426a-a60c-ef262d101a56', '33f2cd33-cf38-4247-9097-75f895d1ef7a', 'eae07b63-9bf4-4e25-80af-df5228dedf35', 'ProjectWidgetMyTasksDueToday', '[{""name"":""userId"",""type"":""guid"",""value"":""{{CurrentUser.Id}}""},{""name"":""currentDate"",""type"":""date"",""value"":""{{CurrentDate}}""}]');");

            // << ***Create page data source*** Name: AllAccounts >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('26443ab3-5dd7-42ef-85c6-8f6a1c271957', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', '61d21547-b353-48b8-8b75-b727680da79e', 'AllAccounts', '[]');");

            // << ***Create page data source*** Name: CurrentDate >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('cb29b5cf-18b4-404c-bd8e-511766624ad7', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', '64207638-d75e-4a25-9965-6e35b0aa835a', 'CurrentDate', '[]');");

            // << ***Create page data source*** Name: TrackTimeTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('9ba5e65b-b10c-4217-8aa8-e2d3db5f22f8', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', '473ee9b6-2131-4164-b5fe-d9b3073e9178', 'TrackTimeTasks', '[{""name"":""search_query"",""type"":""text"",""value"":""{{RequestQuery.q_x_fts_v}}""}]');");

            // << ***Create page data source*** Name: AccountSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('40b84bc0-00bc-422c-a292-e1f805d3ad93', 'd23be591-dbb5-4795-86e4-8adbd9aff08b', '12dcdf08-af03-4347-8015-bd9bace17514', 'AccountSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""AllAccounts""},{""name"":""KeyPropName"",""type"":""text"",""value"":""id""},{""name"":""ValuePropName"",""type"":""text"",""value"":""name""}]');");

            // << ***Create page data source*** Name: NoOwnerTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('0335866b-023d-4922-af27-a27960d72177', 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c', '40c0bcc6-2e3e-4b68-ae6a-27f1f472f069', 'NoOwnerTasks', '[{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy}}""},{""name"":""sortOrder"",""type"":""text"",""value"":""{{RequestQuery.sortOrder}}""},{""name"":""page"",""type"":""int"",""value"":""{{RequestQuery.page}}""},{""name"":""searchQuery"",""type"":""text"",""value"":""{{RequestQuery.q_x_search_v}}""}]');");

            // << ***Create page data source*** Name: FeedItemsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('0b3fefbc-0c11-4d22-8343-8d638165a026', 'acb76466-32b8-428c-81cb-47b6013879e7', '74e5a414-6deb-4af6-8e29-567f718ca430', 'FeedItemsForRecordId', '[{""name"":""recordId"",""type"":""text"",""value"":""{{CurrentUser.Id}}""},{""name"":""type"",""type"":""text"",""value"":""{{RequestQuery.type}}""}]');");

            // << ***Create page data source*** Name: AllOpenTasks >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('952120f1-d736-400c-817a-1f43ac455bc3', '273dd749-3804-48c8-8306-078f1e7f3b3f', '9c2337ac-b505-4ce4-b1ff-ffde2e37b312', 'AllOpenTasks', '[{""name"":""sortBy"",""type"":""text"",""value"":""{{RequestQuery.sortBy}}""},{""name"":""sortOrder"",""type"":""text"",""value"":""{{RequestQuery.sortOrder}}""},{""name"":""page"",""type"":""int"",""value"":""{{RequestQuery.page}}""},{""name"":""searchQuery"",""type"":""text"",""value"":""{{RequestQuery.q_x_search_v}}""}]');");

            // << ***Create page data source*** Name: TaskStatuses >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('39358f5c-122d-40a8-8501-7e944f72ec7d', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', 'fad53f3d-4d3b-4c7b-8cd2-23e96a086ad8', 'TaskStatuses', '[]');");

            // << ***Create page data source*** Name: TaskStatusSelectOptions >>
            migrationBuilder.Sql(@"INSERT INTO page_data_sources (id, page_id, data_source_id, name, parameters) VALUES ('676f2a8a-cbeb-40e7-b9fb-66cfd3cb9a1b', 'e9c8f7ef-4714-40e9-90cd-3814d89603b1', '12dcdf08-af03-4347-8015-bd9bace17514', 'TaskStatusSelectOptions', '[{""name"":""DataSourceName"",""type"":""text"",""value"":""TaskStatuses""}]');");


            // ========================================
            // Data Sources
            // ========================================

            // << ***Create data source*** Name: AllAccounts >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('61d21547-b353-48b8-8b75-b727680da79e', 'AllAccounts', 'Lists all accounts in the system', 10, 'SELECT id,name 
FROM account
where name CONTAINS @name
ORDER BY @sortBy ASC
PAGE @page
PAGESIZE @pageSize', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_account.""id"" AS ""id"",
	 rec_account.""name"" AS ""name"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_account
WHERE  ( rec_account.""name""  ILIKE  @name ) 
ORDER BY rec_account.""name"" ASC
LIMIT 15
OFFSET 0
) X
', '[{""name"":""name"",""type"":""text"",""value"":""null""},{""name"":""sortBy"",""type"":""text"",""value"":""name""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]', 'account');");

            // << ***Create data source*** Name: AllProjects >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('96218f33-42f1-4ff1-926c-b1765e1f8c6e', 'AllProjects', 'all project records', 10, 'SELECT id,abbr,name,$user_1n_project_owner.username
FROM project
WHERE name CONTAINS @filterName
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
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
WHERE  ( rec_project.""name""  ILIKE  @filterName ) 
ORDER BY rec_project.""name"" ASC
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""name""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""filterName"",""type"":""text"",""value"":""null""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_project_owner"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'project');");

            // << ***Create data source*** Name: AllUsers >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('f3e5ab66-9257-42f9-8bdf-f0233dd4aedd', 'AllUsers', 'All system users', 10, 'SELECT *
FROM user
ORDER BY username asc
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_user.""id"" AS ""id"",
	 rec_user.""created_on"" AS ""created_on"",
	 rec_user.""first_name"" AS ""first_name"",
	 rec_user.""last_name"" AS ""last_name"",
	 rec_user.""username"" AS ""username"",
	 rec_user.""email"" AS ""email"",
	 rec_user.""password"" AS ""password"",
	 rec_user.""last_logged_in"" AS ""last_logged_in"",
	 rec_user.""enabled"" AS ""enabled"",
	 rec_user.""verified"" AS ""verified"",
	 rec_user.""preferences"" AS ""preferences"",
	 rec_user.""image"" AS ""image"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_user
ORDER BY rec_user.""username"" ASC
) X
', '[]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""first_name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""last_name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""email"",""type"":6,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""password"",""type"":13,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""last_logged_in"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""verified"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""preferences"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]}]', 'user');");

            // << ***Create data source*** Name: ProjectOpenTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('46aab266-e2a8-4b67-9155-39ec1cf3bccb', 'ProjectOpenTasks', 'All open tasks for a project', 10, 'SELECT *,$milestone_nn_task.name,$task_status_1n_task.label,$task_type_1n_task.label
FROM task
WHERE $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""owner_id"" AS ""owner_id"",
	 rec_task.""start_date"" AS ""start_date"",
	 rec_task.""target_date"" AS ""target_date"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""x_nonbillable_hours"" AS ""x_nonbillable_hours"",
	 rec_task.""x_billable_hours"" AS ""x_billable_hours"",
	 rec_task.""type_id"" AS ""type_id"",
	 rec_task.""priority"" AS ""priority"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $milestone_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 milestone_nn_task.""id"" AS ""id"",
		 milestone_nn_task.""name"" AS ""name""
	 FROM rec_milestone milestone_nn_task
	 LEFT JOIN  rel_milestone_nn_task milestone_nn_task_target ON milestone_nn_task_target.target_id = rec_task.id
	 WHERE milestone_nn_task.id = milestone_nn_task_target.origin_id )d  )::jsonb AS ""$milestone_nn_task"",
	
	-------< $milestone_nn_task	------->: $task_status_1n_task
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 task_status_1n_task.""id"" AS ""id"",
		 task_status_1n_task.""label"" AS ""label"" 
	 FROM rec_task_status task_status_1n_task
	 WHERE task_status_1n_task.id = rec_task.status_id ) d )::jsonb AS ""$task_status_1n_task"",
	
	-------< $task_status_1n_task	------->: $task_type_1n_task
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""projectId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000""},{""name"":""sortBy"",""type"":""text"",""value"":""id""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_date"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""target_date"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_hours"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_hours"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$milestone_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: ProjectAuxData >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('3c5a9d64-47ea-466a-8b0e-49e61df58bd1', 'ProjectAuxData', 'getting related data for the current project', 10, 'SELECT $user_1n_project_owner.id
FROM project
WHERE id = @recordId
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 COUNT(*) OVER() AS ___total_count___,
	------->: $user_1n_project_owner
	(SELECT  COALESCE( array_to_json( array_agg( row_to_json(d) )), ''[]'') FROM ( 
	 SELECT 
		 user_1n_project_owner.""id"" AS ""id"" 
	 FROM rec_user user_1n_project_owner
	 WHERE user_1n_project_owner.id = rec_project.owner_id ) d )::jsonb AS ""$user_1n_project_owner""	
	-------< $user_1n_project_owner
FROM rec_project
WHERE  ( rec_project.""id"" = @recordId ) 
) X
', '[{""name"":""recordId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000""}]', '[{""name"":""$user_1n_project_owner"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'project');");

            // << ***Create data source*** Name: TaskStatuses >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('fad53f3d-4d3b-4c7b-8cd2-23e96a086ad8', 'TaskStatuses', 'All task statuses', 10, 'SELECT *
FROM task_status
ORDER BY label asc', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task_status.""id"" AS ""id"",
	 rec_task_status.""is_closed"" AS ""is_closed"",
	 rec_task_status.""is_default"" AS ""is_default"",
	 rec_task_status.""l_scope"" AS ""l_scope"",
	 rec_task_status.""label"" AS ""label"",
	 rec_task_status.""sort_index"" AS ""sort_index"",
	 rec_task_status.""is_system"" AS ""is_system"",
	 rec_task_status.""is_enabled"" AS ""is_enabled"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_task_status
ORDER BY rec_task_status.""label"" ASC
) X
', '[]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_closed"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_default"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sort_index"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_system"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]}]', 'task_status');");

            // << ***Create data source*** Name: ProjectWidgetMyTasksDueToday >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('eae07b63-9bf4-4e25-80af-df5228dedf35', 'ProjectWidgetMyTasksDueToday', 'My tasks due today', 10, 'SELECT *,$project_nn_task.name
FROM task
WHERE owner_id = @userId AND end_time > @currentDateStart AND end_time < @currentDateEnd AND status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf''
ORDER BY priority DESC
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""name"" AS ""name""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task""	
	-------< $project_nn_task

FROM rec_task
WHERE  (  (  (  ( rec_task.""owner_id"" = @userId )  AND  ( rec_task.""end_time"" > @currentDateStart )  )  AND  ( rec_task.""end_time"" < @currentDateEnd )  )  AND  ( rec_task.""status_id"" <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' )  ) 
ORDER BY rec_task.""priority"" DESC
) X
', '[{""name"":""userId"",""type"":""guid"",""value"":""guid.empty""},{""name"":""currentDateStart"",""type"":""date"",""value"":""now""},{""name"":""currentDateEnd"",""type"":""date"",""value"":""now""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: TaskTypes >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('4857ace4-fcfc-4803-ad86-7c7afba91ce0', 'TaskTypes', 'All task types', 10, 'SELECT *
FROM task_type
WHERE l_scope CONTAINS @scope
ORDER BY sort_index asc', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task_type.""id"" AS ""id"",
	 rec_task_type.""is_default"" AS ""is_default"",
	 rec_task_type.""l_scope"" AS ""l_scope"",
	 rec_task_type.""label"" AS ""label"",
	 rec_task_type.""sort_index"" AS ""sort_index"",
	 rec_task_type.""is_system"" AS ""is_system"",
	 rec_task_type.""is_enabled"" AS ""is_enabled"",
	 rec_task_type.""icon_class"" AS ""icon_class"",
	 rec_task_type.""color"" AS ""color"",
	 COUNT(*) OVER() AS ___total_count___
FROM rec_task_type
WHERE  ( rec_task_type.""l_scope""  ILIKE  @scope ) 
ORDER BY rec_task_type.""sort_index"" ASC
) X
', '[{""name"":""scope"",""type"":""text"",""value"":""projects""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_default"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""sort_index"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_system"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_enabled"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]', 'task_type');");

            // << ***Create data source*** Name: AllTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('5a6e9d56-63bc-43b1-b95e-24838db9f435', 'AllTasks', 'All tasks selection', 10, 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: TaskComments >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('f68fa8be-b957-4692-b459-4da62d23f472', 'TaskComments', 'All comments for a certain task', 10, 'SELECT *,$task_nn_comment.id,$task_nn_comment.$project_nn_task.id,$case_nn_comment.id FROM comment
WHERE id = @commentId', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_comment.""id"" AS ""id"",
	 rec_comment.""body"" AS ""body"",
	 rec_comment.""created_by"" AS ""created_by"",
	 rec_comment.""created_on"" AS ""created_on"",
	 rec_comment.""l_scope"" AS ""l_scope"",
	 rec_comment.""parent_id"" AS ""parent_id"",
	 COUNT(*) OVER() AS ___total_count___,
	------->: $task_nn_comment
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 task_nn_comment.""id"" AS ""id"",
		------->: $project_nn_task
		(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
		 SELECT 
			 project_nn_task.""id"" AS ""id""
		 FROM rec_project project_nn_task
		 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = task_nn_comment.id
		 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task""		
		-------< $project_nn_task

	 FROM rec_task task_nn_comment
	 LEFT JOIN  rel_task_nn_comment task_nn_comment_target ON task_nn_comment_target.target_id = rec_comment.id
	 WHERE task_nn_comment.id = task_nn_comment_target.origin_id )d  )::jsonb AS ""$task_nn_comment"",
	-------< $task_nn_comment
	------->: $case_nn_comment
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 case_nn_comment.""id"" AS ""id""
	 FROM rec_case case_nn_comment
	 LEFT JOIN  rel_case_nn_comment case_nn_comment_target ON case_nn_comment_target.target_id = rec_comment.id
	 WHERE case_nn_comment.id = case_nn_comment_target.origin_id )d  )::jsonb AS ""$case_nn_comment""	
	-------< $case_nn_comment

FROM rec_comment
WHERE  ( rec_comment.""id"" = @commentId ) 
) X
', '[{""name"":""commentId"",""type"":""guid"",""value"":""d5e1d939-fa3e-4332-a521-4c4e0f051e8a""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$task_nn_comment"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]},{""name"":""$case_nn_comment"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'comment');");

            // << ***Create data source*** Name: CommentsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('a588e096-358d-4426-adf6-5db693f32322', 'CommentsForRecordId', 'Get all comments for a record', 10, 'SELECT *,$user_1n_comment.image,$user_1n_comment.username
FROM comment
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_comment.""id"" AS ""id"",
	 rec_comment.""body"" AS ""body"",
	 rec_comment.""created_by"" AS ""created_by"",
	 rec_comment.""created_on"" AS ""created_on"",
	 rec_comment.""l_scope"" AS ""l_scope"",
	 rec_comment.""parent_id"" AS ""parent_id"",
	 rec_comment.""l_related_records"" AS ""l_related_records"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":4,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_comment"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'comment');");

            // << ***Create data source*** Name: AllProjectTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd', 'AllProjectTasks', 'All tasks in a project', 10, 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE x_search CONTAINS @searchQuery AND $project_nn_task.id = @projectId
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""},{""name"":""projectId"",""type"":""guid"",""value"":""guid.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: TimeLogsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('e66b8374-82ea-4305-8456-085b3a1f1f2d', 'TimeLogsForRecordId', 'Get all time logs for a record', 10, 'SELECT *,$user_1n_timelog.image,$user_1n_timelog.username
FROM timelog
WHERE l_related_records CONTAINS @recordId 
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_timelog.""id"" AS ""id"",
	 rec_timelog.""body"" AS ""body"",
	 rec_timelog.""created_by"" AS ""created_by"",
	 rec_timelog.""created_on"" AS ""created_on"",
	 rec_timelog.""is_billable"" AS ""is_billable"",
	 rec_timelog.""l_related_records"" AS ""l_related_records"",
	 rec_timelog.""l_scope"" AS ""l_scope"",
	 rec_timelog.""minutes"" AS ""minutes"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":10,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""is_billable"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_timelog"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'timelog');");

            // << ***Create data source*** Name: FeedItemsForRecordId >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('74e5a414-6deb-4af6-8e29-567f718ca430', 'FeedItemsForRecordId', 'Get all feed items for a record', 10, 'SELECT *,$user_1n_feed_item.image,$user_1n_feed_item.username
FROM feed_item
WHERE l_related_records CONTAINS @recordId AND type CONTAINS @type
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_feed_item.""id"" AS ""id"",
	 rec_feed_item.""created_by"" AS ""created_by"",
	 rec_feed_item.""created_on"" AS ""created_on"",
	 rec_feed_item.""l_scope"" AS ""l_scope"",
	 rec_feed_item.""subject"" AS ""subject"",
	 rec_feed_item.""body"" AS ""body"",
	 rec_feed_item.""type"" AS ""type"",
	 rec_feed_item.""l_related_records"" AS ""l_related_records"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""created_on""},{""name"":""sortOrder"",""type"":""text"",""value"":""desc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""recordId"",""type"":""text"",""value"":""string.empty""},{""name"":""type"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_related_records"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$user_1n_feed_item"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""image"",""type"":9,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'feed_item');");

            // << ***Create data source*** Name: NoOwnerTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('40c0bcc6-2e3e-4b68-ae6a-27f1f472f069', 'NoOwnerTasks', 'all tasks without an owner', 10, 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE owner_id = NULL AND x_search CONTAINS @searchQuery AND status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf''
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: TaskAuxData >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('587d963b-613f-4e77-a7d4-719f631ce6b2', 'TaskAuxData', 'getting related data for the current task', 10, 'SELECT $project_nn_task.id,$project_nn_task.abbr,$project_nn_task.name,$user_nn_task_watchers.id
FROM task
WHERE id = @recordId', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""abbr"" AS ""abbr"",
		 project_nn_task.""name"" AS ""name""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task"",
	-------< $project_nn_task
	------->: $user_nn_task_watchers
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 user_nn_task_watchers.""id"" AS ""id""
	 FROM rec_user user_nn_task_watchers
	 LEFT JOIN  rel_user_nn_task_watchers user_nn_task_watchers_target ON user_nn_task_watchers_target.target_id = rec_task.id
	 WHERE user_nn_task_watchers.id = user_nn_task_watchers_target.origin_id )d  )::jsonb AS ""$user_nn_task_watchers""	
	-------< $user_nn_task_watchers

FROM rec_task
WHERE  ( rec_task.""id"" = @recordId ) 
) X
', '[{""name"":""recordId"",""type"":""guid"",""value"":""00000000-0000-0000-0000-000000000000""}]', '[{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_nn_task_watchers"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: AllOpenTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('9c2337ac-b505-4ce4-b1ff-ffde2e37b312', 'AllOpenTasks', 'All open tasks selection', 10, 'SELECT *,$project_nn_task.abbr,$user_1n_task.username,$task_status_1n_task.label,$task_type_1n_task.label,$task_type_1n_task.icon_class,$task_type_1n_task.color,$user_1n_task_creator.username
FROM task
WHERE status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' AND x_search CONTAINS @searchQuery
ORDER BY @sortBy @sortOrder
PAGE @page
PAGESIZE @pageSize', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
LIMIT 15
OFFSET 0
) X
', '[{""name"":""sortBy"",""type"":""text"",""value"":""end_time""},{""name"":""sortOrder"",""type"":""text"",""value"":""asc""},{""name"":""page"",""type"":""int"",""value"":""1""},{""name"":""pageSize"",""type"":""int"",""value"":""15""},{""name"":""searchQuery"",""type"":""text"",""value"":""string.empty""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""abbr"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_status_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$task_type_1n_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""label"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""icon_class"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""color"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]},{""name"":""$user_1n_task_creator"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""username"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: ProjectWidgetMyTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('c44eab77-c81e-4f55-95c8-4949b275fc99', 'ProjectWidgetMyTasks', 'top 5 upcoming tasks', 10, 'SELECT *,$project_nn_task.name
FROM task
WHERE owner_id = @userId AND (end_time > @currentDate OR end_time = null) AND status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf''
ORDER BY end_time ASC, priority DESC
PAGE 1
PAGESIZE 5
', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""name"" AS ""name""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task""	
	-------< $project_nn_task

FROM rec_task
WHERE  (  (  ( rec_task.""owner_id"" = @userId )  AND  (  ( rec_task.""end_time"" > @currentDate )  OR  ( rec_task.""end_time"" IS NULL )  )  )  AND  ( rec_task.""status_id"" <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' )  ) 
ORDER BY rec_task.""end_time"" ASC , rec_task.""priority"" DESC
LIMIT 5
OFFSET 0
) X
', '[{""name"":""userId"",""type"":""guid"",""value"":""guid.empty""},{""name"":""currentDate"",""type"":""date"",""value"":""now""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

            // << ***Create data source*** Name: ProjectWidgetMyOverdueTasks >>
            migrationBuilder.Sql(@"INSERT INTO data_sources (id, name, description, weight, eql_text, sql_text, parameters_json, fields_json, entity_name) VALUES ('946919a6-e4cd-41a2-97dc-1069d73adcd1', 'ProjectWidgetMyOverdueTasks', 'all my overdue tasks', 10, 'SELECT *,$project_nn_task.name
FROM task
WHERE owner_id = @userId AND end_time < @currentDate AND status_id <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf''
ORDER BY end_time ASC, priority DESC ', 'SELECT row_to_json( X ) FROM (
SELECT DISTINCT 
	 rec_task.""id"" AS ""id"",
	 rec_task.""l_scope"" AS ""l_scope"",
	 rec_task.""subject"" AS ""subject"",
	 rec_task.""body"" AS ""body"",
	 rec_task.""created_on"" AS ""created_on"",
	 rec_task.""created_by"" AS ""created_by"",
	 rec_task.""completed_on"" AS ""completed_on"",
	 rec_task.""number"" AS ""number"",
	 rec_task.""parent_id"" AS ""parent_id"",
	 rec_task.""status_id"" AS ""status_id"",
	 rec_task.""key"" AS ""key"",
	 rec_task.""x_search"" AS ""x_search"",
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
	 COUNT(*) OVER() AS ___total_count___,
	------->: $project_nn_task
	(SELECT  COALESCE(  array_to_json(array_agg( row_to_json(d))), ''[]'') FROM ( 
	 SELECT 
		 project_nn_task.""id"" AS ""id"",
		 project_nn_task.""name"" AS ""name""
	 FROM rec_project project_nn_task
	 LEFT JOIN  rel_project_nn_task project_nn_task_target ON project_nn_task_target.target_id = rec_task.id
	 WHERE project_nn_task.id = project_nn_task_target.origin_id )d  )::jsonb AS ""$project_nn_task""	
	-------< $project_nn_task

FROM rec_task
WHERE  (  (  ( rec_task.""owner_id"" = @userId )  AND  ( rec_task.""end_time"" < @currentDate )  )  AND  ( rec_task.""status_id"" <> ''b1cc69e5-ce09-40e0-8785-b6452b257bdf'' )  ) 
ORDER BY rec_task.""end_time"" ASC , rec_task.""priority"" DESC
) X
', '[{""name"":""userId"",""type"":""guid"",""value"":""guid.empty""},{""name"":""currentDate"",""type"":""date"",""value"":""now""}]', '[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""l_scope"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""subject"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""body"",""type"":8,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""created_by"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""completed_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""number"",""type"":1,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""parent_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""status_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""key"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_search"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""estimated_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_billable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""x_nonbillable_minutes"",""type"":12,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""priority"",""type"":17,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""timelog_started_on"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""owner_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""type_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""start_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""end_time"",""type"":5,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""recurrence_id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""reserve_time"",""type"":2,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""$project_nn_task"",""type"":20,""entity_name"":"""",""relation_name"":null,""children"":[{""name"":""id"",""type"":16,""entity_name"":"""",""relation_name"":null,""children"":[]},{""name"":""name"",""type"":18,""entity_name"":"""",""relation_name"":null,""children"":[]}]}]', 'task');");

        }

        /// <summary>
        /// Reverses the initial project schema migration.
        /// Removes all project application metadata in reverse dependency order:
        /// page data sources, data sources, page body nodes, pages, sitemap nodes, sitemap areas, application.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ========================================
            // Remove Page Data Sources
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '676f2a8a-cbeb-40e7-b9fb-66cfd3cb9a1b';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '39358f5c-122d-40a8-8501-7e944f72ec7d';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '952120f1-d736-400c-817a-1f43ac455bc3';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '0b3fefbc-0c11-4d22-8343-8d638165a026';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '0335866b-023d-4922-af27-a27960d72177';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '40b84bc0-00bc-422c-a292-e1f805d3ad93';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '9ba5e65b-b10c-4217-8aa8-e2d3db5f22f8';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'cb29b5cf-18b4-404c-bd8e-511766624ad7';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '26443ab3-5dd7-42ef-85c6-8f6a1c271957';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f1da592e-d696-426a-a60c-ef262d101a56';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '7717b418-7eed-472a-b4cd-6ada2e85d6df';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f5a2f77f-6d79-4180-b73f-7deb21895f4e';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'e688cbdd-0fa9-43b4-aed1-3d667fdecf87';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '361dc0a8-68b8-45ec-8002-11779a304899';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '24e093ae-ab0f-4c52-86b2-9e1fe2ed2a0a';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '2f523831-0437-4250-a6b5-8eeb3da9d04c';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'ee65976e-d5d0-4dd4-ac6a-2047e8817add';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'cf3b936e-ec45-4937-a157-a008ef97d594';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '120c783a-f04c-4be9-a9ef-f991aae3d648';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '9e50f76d-f56c-4204-9d8b-4db8860371a5';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '9c8ec6cc-b389-4baa-b4ce-770edf2520dd';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f09fe186-8617-4f94-a67b-3a69172b1257';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '750213cb-8c69-4749-b10f-211b53369958';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '43691d9f-65ef-433c-934b-ccf6eaafdd3f';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '5ff5cc0c-c06e-4b58-8a31-4714914778aa';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f8c429ee-c6fe-457d-9339-44e626a6dd27';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '6c41cbd7-d99f-4019-84f0-24361bfd7a0a';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'e58cc762-4e3f-4b6f-9968-f3b6ed907a86';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '561c85b5-b016-4420-8770-9752ff5347b9';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'c4bb6351-2fa9-4953-852f-62eb782e839c';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'ebf5c697-3a01-4759-b9c6-ec7f3414bb54';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'defaf774-60d6-4c15-9683-da15ca53730c';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'f92520fe-8ea9-4284-a991-bb74810660e5';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'fefbdab5-57ee-4343-9355-199c154bde3d';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '8b29596b-3310-46e0-838b-682e243f4611';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '7d05f40e-71ae-49de-9dd0-2231b1c9265a';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '1487e7c6-60b2-4c2c-9ebe-0648435d2330';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '6e38b5c3-43ba-4d5e-8454-11e7f6eef235';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'a94b7669-edd2-484e-88fb-d480f79b4ec6';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = '993d643a-1c10-4475-8b1f-3e5ac5f2e036';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'd13ee96e-64e6-4174-b16d-c1c5a7bcb9f9';");
            migrationBuilder.Sql(@"DELETE FROM page_data_sources WHERE id = 'a2db7724-f05b-4820-9269-64792398c309';");

            // ========================================
            // Remove Data Sources
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '946919a6-e4cd-41a2-97dc-1069d73adcd1';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'c44eab77-c81e-4f55-95c8-4949b275fc99';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '9c2337ac-b505-4ce4-b1ff-ffde2e37b312';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '587d963b-613f-4e77-a7d4-719f631ce6b2';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '40c0bcc6-2e3e-4b68-ae6a-27f1f472f069';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '74e5a414-6deb-4af6-8e29-567f718ca430';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'e66b8374-82ea-4305-8456-085b3a1f1f2d';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'c2284f3d-2ddc-4bad-9d1b-f6e44d502bdd';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'a588e096-358d-4426-adf6-5db693f32322';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'f68fa8be-b957-4692-b459-4da62d23f472';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '5a6e9d56-63bc-43b1-b95e-24838db9f435';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '4857ace4-fcfc-4803-ad86-7c7afba91ce0';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'eae07b63-9bf4-4e25-80af-df5228dedf35';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'fad53f3d-4d3b-4c7b-8cd2-23e96a086ad8';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '3c5a9d64-47ea-466a-8b0e-49e61df58bd1';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '46aab266-e2a8-4b67-9155-39ec1cf3bccb';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = 'f3e5ab66-9257-42f9-8bdf-f0233dd4aedd';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '96218f33-42f1-4ff1-926c-b1765e1f8c6e';");
            migrationBuilder.Sql(@"DELETE FROM data_sources WHERE id = '61d21547-b353-48b8-8b75-b727680da79e';");

            // ========================================
            // Remove Page Body Nodes
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd1b4df6b-5ce7-4831-8d59-efd2cfdd4d51';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '87e08d83-c8fb-4a41-a371-1316b6da0b17';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fdf94ef9-2130-4600-a3b1-53d1cb5489fc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bd05d5ef-0ab4-48b0-a40e-5959875d071b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '35cb5466-3654-426d-97cd-caa83bb5ed3e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'eb2e70db-8215-4097-a0f5-bc5154f21153';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '2a4c131b-69b8-43bf-bf0f-7360cd953797';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a38a8496-5f14-4c23-bd04-f036c4629824';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a4719fbd-b3d0-4f81-b302-96f5620e17cc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ecd7a737-fc1a-4766-9ea4-81ef60f099aa';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9fa77e2f-c21d-48ef-82ea-825eaa697412';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9d6caedb-f43a-4ccb-a747-4c8917a6471e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8012f7aa-e60b-4db9-a380-374c9238c12b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8d244aa4-ad6b-464f-87e9-37a55fd18d19';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b2b5e677-341a-43af-9baa-0aba98a7d8c3';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0867158f-f0c2-4284-838a-1c4ec3acb796';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0ba5ed3f-625e-4df3-84ab-70f064b9905a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fd73e317-ae0a-4c54-9bed-55f9c89965a9';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '001a3188-1d23-4f85-90f2-9053eac93bbc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd0addd75-c216-4f25-9f61-44fb29f7f160';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '91bbd374-13d5-4a86-8a07-84349ec57682';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '34916453-4d5a-40a7-b74c-3c4e8b5a8950';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8a75b1d8-8184-40ed-a977-26616239fbb7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b2caeb51-b6a5-4e15-a317-9825511792c6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '30676929-f280-414d-8f4c-d41f851136ce';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b37c63a7-84ea-4673-9a81-ec4313c178b7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7a7fbcd5-fb6f-40fd-a0cd-1a7c26e1c4ab';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '551483ab-262b-4541-b0dc-fadaa8de5284';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6de13934-ca81-4807-bb71-cadcdbb99ca7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ff5b4808-9c2a-4d4f-8eaf-a4878594c55a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c150a0fa-9c1a-4f05-a842-22d374e2c2e6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9888344d-c88f-4d1a-9984-7a718779e4cc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd1580be1-733d-477e-bd4a-65e325a8a263';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '73d24cb2-ae13-4ddd-9ea8-80d8ef6c2911';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a918fec1-865b-4c54-8f93-685ffe85fb90';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5899b892-ee3d-4cb9-9811-a24ff8f1b791';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0660124a-cbf0-47ff-9757-3f072c39953a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ad9c357f-e620-4ed1-9593-d76c97019677';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e5756351-b9c2-4bd9-bcbc-be3cc9fb3751';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '08d8dad6-594d-498f-aa0b-555d245ce9e2';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9bdd70b0-aa1d-4458-ad95-9fc455236350';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '15df96da-8d77-427f-a2a1-23017a6f8800';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8b4b07e4-b994-4fdc-95d4-1e7b33dea6dc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e1b676c0-e128-46a2-b2cc-51a5b3ec2816';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '250115da-cea5-46f3-a77a-d2f7704c650d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3f85bfe4-5040-42c6-a3fb-fefc9ab59b10';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a7720205-8f62-4319-98c3-17c6e3a0462b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd4c56ca4-52f8-47b8-8d62-e5a43930b377';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bfd0dba8-dc50-4881-9815-7f5e56a6a2fb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0c32036a-4432-4b17-beb7-198ba22ea134';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '70424cd2-2b69-4c87-9977-cb60a72239fd';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '2e98b41a-f845-4ab1-aeaf-944ae963883b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7eff5a2c-5d5d-4989-a68f-a1362b0dad7c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ffac14be-00ee-4a72-a08e-f5b0956171c4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9946104a-a6ec-4a0b-b996-7bc630c16287';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '418b58a8-88d2-4dfc-b4a9-22617dab76c4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd11a258c-2ad3-4421-84db-990aa7683a2d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9dcca796-cb6d-4c7f-bb63-761cff4c218a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6b0ee717-f4af-47fb-b441-125a755af01b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6b3e9fec-7fc1-4455-8dcc-a3b67f4ca427';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'cd70f0e3-be35-4f94-8894-c8f26e021d88';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3658981b-cef7-4938-9c3a-a13cd5b760a0';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd6b5ad6d-4455-4828-bc46-b072aa4919f5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '25ac7fb9-2737-428d-9678-90222252c024';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6694f852-c49e-4dd2-a4dc-dd2f6faaf4b4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a066475d-c2ff-4e59-9481-08cd637f71ca';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ca7a9302-afc3-4688-9748-676211bcddb3';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b14bb20c-fab7-40a4-8feb-8a899b761dda';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '630bea4c-bccf-4587-83f7-6d0d2ed5bac0';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '319c5697-21c9-4799-ae6f-343586f5d2cf';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bef8058a-2c62-47a8-abd3-813636ebd4a8';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '156877b1-d1ea-4fea-be4a-62a982bef3a7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'cb0e42ee-aa06-4a92-8bb0-940e7332411e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '27a635a7-143c-4a28-bf08-601771a453c1';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '39fa86aa-3d7a-49af-bfa9-30f1c03671eb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'becc4486-be49-4fa6-9d3a-0d8e15606fcc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f1c53374-0efb-4612-ab94-68e8e8242ddb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8fbba43e-cd4d-4b0d-9e3e-7d19a2cc8468';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '734e7201-a15e-4ae5-8ea9-b683a94f80d0';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4c58c1bb-321a-41c6-954f-4c6fafe6661c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5466f4d1-20a5-4808-8bb5-aefaac756347';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bcf153c5-8c7c-4ac8-a5d9-04e288ff7ccf';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a94793d2-492a-4b7e-9fac-199f8bf46f46';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0c29732a-945e-4bbc-9486-b86efb2897b2';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6bf0435c-f9c0-44b7-a801-00222cd7c0bb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '66828292-07c7-4cc1-9060-a92798d6b95a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7e2d1d10-a9cc-4eae-b3d6-a30ab3647102';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd7ef95ce-8508-4722-a5f0-3d114bda4585';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7612914f-21ea-4665-9b66-385cf1cafb41';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '1739a2f0-76ba-4343-a344-9b0564096d06';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '30e99568-2727-4ce4-8da6-c97feaaf4432';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fe59f151-1e79-4df2-a034-9f45f1dcd691';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fb3d0142-e080-43ef-ba31-a79d0221c0df';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '23200f02-439a-4719-ae0e-498c9dcde58c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e03e40c2-dae2-4351-947c-02295a064328';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4a588a7d-ea03-4be1-ab0d-3120d98c3548';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ecef4b2c-6988-44c1-acea-0e28385ec528';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '884a8db1-aff0-4f86-ab7d-8fb17698fc33';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a1110167-15bd-46b7-ae3c-cc8ba87be98f';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '1af3c0cb-a58e-4d19-89a2-2ce4b8e60945';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '48105732-6025-4614-9065-55647afa9b96';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bd3ed9ae-90aa-4373-9eb9-cc677353bc6d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b6951134-f57f-4da2-8203-a8c36cc99fd7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '719ca43f-bb66-4134-a2d2-ad2cc30ade6d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'edc68b26-d508-4c2e-a431-5a6656957944';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4a059596-3804-435e-b535-2da1f56abb29';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fac5a2f6-b1b4-402a-bf0d-e0a3fb4dd36a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7f01d2c0-2542-4b88-b8f0-711947e4d0c6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0dbdb202-7288-49e6-b922-f69e947590e5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'be6aa619-e380-4bf9-b279-47dda4d5f4eb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6029e40b-0835-460f-b782-1e4228ea4234';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '2684f725-38e2-4f8c-92ee-e3b1ccf04aff';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0c295451-1c38-4eb0-8000-feefe912a667';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '94be4b02-07ea-4a54-a6fc-89316fa1e90a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5891b0d8-6750-4502-bd8e-fe1380f08b0c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '94bfe723-e5f6-478d-afb6-504edf2bdc2b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '86506ad8-e1cb-4b46-84b9-881e0326ebaa';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '302de86f-7178-4e2b-9ac1-d447163a9558';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'df7c7cab-0e16-4e75-bb13-04666afeff81';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '747c108b-ed45-46f3-b06a-113e2490888d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '2d3dddf7-cefb-4073-977f-4e1b6bf8935e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8cbdfd1a-5d0e-4961-8e79-74072c133202';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '89cb4088-ea04-4ce2-8cbe-5367c5741ef3';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3ad8d6e5-eed7-44b7-95e1-12f22714037b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6f8f9a9a-a464-4175-9178-246b792738a6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ee526509-7840-498a-9c1f-8a69d80c5f2e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '1fff4a92-d045-4019-b27c-bccb1fd1cb82';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '70a864dc-8311-4dd3-bc13-1a3b87821e30';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b105d13c-3710-4ace-b51f-b57323912524';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b2935724-bfcc-4821-bdb2-81bc9b14f015';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '291477ec-dd9c-4fc3-97a0-d2fd62809b2f';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '798e39b3-7a36-406b-bed6-e77da68fc50f';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '651e5fb2-56df-4c46-86b3-19a641dc942d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '27843f6e-43ed-49e7-9cc5-ec35393e93f4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9f15bb3a-b6bf-424c-9394-669cc2041215';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f2175b92-4941-4cbe-ba4b-305167b6738b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '101245d5-1ff9-4eb3-ba28-0b29cb56a0ec';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bbe36a16-9210-415b-95f3-912482d27fd2';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ecc262e9-fbad-4dd1-9c98-56ad047685fb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3e15a63d-8f5f-4357-a692-b5998c31d543';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8099e123-1218-4008-b8e6-8ff56678d64a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '05459068-33a7-454e-a871-94f9ddc6e5d5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '164261ae-2df4-409a-8fdd-adc85c86a6dc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '452a6f4c-b415-409a-b9b6-a2918a137299';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd076f406-7ddd-4feb-b96a-137e10c2d14e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ddde395b-6cee-4907-a220-a8424e091b13';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '857698b9-f715-480a-bd74-29819a4dec2d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'aa94aac4-5048-4d82-95b2-b38536028cbb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6e918333-a2fa-4cf7-9ca8-662e349625a7';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '754bf941-df31-4b13-ba32-eb3c7a8c8922';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e15e2d00-e704-4212-a7d2-ee125dd687a6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '50f07e9e-65a5-4feb-bf2a-4f12712305c2';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c1e88619-37f4-4dd6-ae9b-f714191d02e3';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'bf38fcf4-adeb-4388-ad4b-6aa4485f9258';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7bc83302-6b26-46ef-a6a1-3e656527faef';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f0de1f0c-b71d-4002-a547-4e6d08654ea8';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5c8d449d-95b8-419b-9851-b9a227e7093b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0e6f5387-b9c4-4fdd-9349-73e8424c6788';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8dc4fd15-a1eb-4b7e-a1a9-381ac8e7de9b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'aec39a46-526c-45f2-ad43-38618a366098';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ace9e1bf-47bf-495f-8e6b-7683d2a0fa78';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6529686c-c8b4-40f0-8242-e24153657be2';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'cc487c98-c59f-4e8c-b147-36914bcf70fc';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7bbf3667-a26d-48d4-8eba-8ca5e03d14c3';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0f90af36-8f2d-4f26-8ba2-ea7e8accdc6d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fc423988-297c-457d-a14b-9fe12557cc2e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4dfaa373-e250-4a76-b5a5-98d596a52313';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e6c5b22a-491a-4186-82d6-667253e2db0f';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8e4e7f05-8942-4db1-8514-e460bde1e2b4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b5a15dac-a606-4c93-b258-f1a7ab799a05';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '39db266a-da49-4a6e-b74d-898c601ad78b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '54d22f88-7a46-41e7-89b4-603dc14e7e73';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ec508ea2-2332-40f0-838c-52d3ee250122';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'fcd1e0a0-bfc3-422f-b19d-2536dd919289';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '31a4f843-0ab5-4fd1-86ee-ad5f23f0d47a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '22af9111-4f15-48c1-a9fd-e5ab72074b3e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ec6f4bb5-aeeb-4706-a3dd-f3f208c63c6a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3845960e-4fc6-40f6-9ef6-36e7392f8ab0';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '492d9088-16bc-40fd-963b-8a8c2acf0ffa';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '81fda9cf-04d7-4f99-8448-34392e1c0640';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '552a4fad-5236-4aad-b3fc-443a5f12e574';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7eb7af4f-bdd3-410a-b3c4-71e620b627c5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '03d2ed0f-33ed-4b7d-84fb-102f4b7452a8';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5ecb652c-e474-4700-bc32-5173d2fdad00';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '0fb05f08-6066-4de8-8452-c8b3c7306ff9';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '037ee1a4-e26c-4cd1-91ca-0e626c2995ed';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b7b8ed33-910f-4d28-bbe8-48c0799b00b5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'aeacecd8-8b3e-4cdb-84f6-114a2fb3c06d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd32f39bb-8ad4-438d-a8d1-7abca6f5e6b4';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4603466b-422c-4666-9f05-aae386569590';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '55ff1b2f-43d4-4bde-818c-fd139d799261';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '278b4db1-b310-416a-9f32-66ecd3475ba8';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b2baa937-e32a-4a06-8b9b-404f89e539c0';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c0962d97-a609-498b-9b0c-7c0dbfae8b73';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6b80c95e-a06d-4ad3-ae19-dfdc9fecf6ed';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c57d94a6-9c90-4071-b54b-2c05b79aa522';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '700954cc-7407-4b20-81de-a882380e5d4d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '46657d5a-0102-43b7-9ca3-9259953d37b6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '3d0eb8a7-1182-4974-a039-433954aa8d7c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e84c527a-4feb-4d60-ab91-4b1ecd89b39c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c50ad432-98f2-4140-a40c-3157fc52f93c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '33cba2bb-6070-4b00-ba92-64064077a49b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd65f22f5-6644-4ca9-81ce-c3ce5898f8b5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '4f298a92-4592-4714-948e-eaebb3962785';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a20664ce-a3fe-436a-84f0-42f4e14564c1';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ad1e60ec-813c-4b1d-aa33-ad76d705d5d9';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '9cd708aa-60c5-4dfa-b95c-73e5508aec64';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'cfa8a277-5447-45f2-ad06-26818381b54a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e83f6542-f9f8-4fec-aeb8-48731951f182';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '064ea82a-c5c2-40dd-96e4-7859aa879b14';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd088ba1c-15b8-48b9-8673-a871338cbdea';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c984f52a-5121-471d-ae66-e8a64de68c3d';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd3501ea7-86f2-4230-8bc5-30ffab78be5e';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f68e4fb5-64d1-48ff-8846-e0ec36aa7e69';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ec0da060-7367-4263-b3fa-7c32765c97c5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6ef7bbd7-b96c-45d4-97e1-b8e43f489ed5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c510d93d-e3d5-40d2-9655-73a3d2f63020';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e49cf2f9-82b0-4988-aa29-427e8d9501d9';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f35b6e4b-3c81-409c-8f01-18e0d457e9ff';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8e533c53-0bf5-4082-ae06-f47f1bd9b3b5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '209d32c9-6c2f-4f45-859a-3ae2718ebf88';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'be907fa3-0971-45b5-9dcf-fabbb277fe54';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '5b95ff72-dfc0-4a99-ad3a-6c6107f7bd4c';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '47303562-04a3-4935-b228-aaa61527f963';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '151e265c-d3d3-4340-92fc-0cace2ca45f9';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '483e09f0-98c4-4e70-ad9a-3a92abebaf74';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'ae930e6f-38b5-4c48-a17f-63b0bdf7dab6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'd4c6bc3b-51d5-4f2d-a329-f02c59250a41';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '63daa5c0-ed7f-432e-bfbb-746b94207146';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'a584a5ed-96a2-4a28-95e8-23266bc36926';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'e1e493ac-6b74-490f-a0e3-ffd2f2f71f1b';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'b9258d04-360b-426f-b542-ec458f946edf';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '77abedcf-4bea-46f3-b50c-340a7aa237d6';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '13a1d868-93ee-41d1-bb94-231d99899f74';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'c0689f85-235d-484e-bea3-e534e6e10094';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '8f61ba2d-9c8a-434d-9f78-d12926cd80ef';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'dedd97f6-1b09-4942-aae1-684cdc49a3eb';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '57789d88-e897-4b7b-9999-239821db4274';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'df667b11-30ac-4b6b-a12d-41e5aaf6cae5';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '7590ab09-b749-4051-935a-b51d16d7b76a';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = 'f4f2b086-1181-4db5-b78f-51d1b41e1611';");
            migrationBuilder.Sql(@"DELETE FROM page_body_nodes WHERE id = '6bb17b95-258a-4572-99f3-898d1895cfba';");

            // ========================================
            // Remove Pages
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '6f673561-fad7-4844-8262-589834f1b2ce';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'd23be591-dbb5-4795-86e4-8adbd9aff08b';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'dfe56667-174d-492d-8f84-b8ab8b70c63f';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '6d3fe557-59dd-4a2e-b710-f3f326ae172b';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'e9c8f7ef-4714-40e9-90cd-3814d89603b1';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'acb76466-32b8-428c-81cb-47b6013879e7';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'd4b31a98-b1ed-44b5-aa69-32a6fc87205e';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '80b10445-c850-44cf-9c8c-57daca671dcf';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '2f11031a-41da-4dfc-8e40-ddc6dca71e2c';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'db1cfef5-50a9-42ba-8f5e-34f80e6aad3c';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '3a40b8e6-0a87-4eee-9b6b-6c665ebee28c';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '68100014-1fd7-456c-9b26-27aa9f858287';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'd07cbf70-09c6-47ee-9a13-80568e43d331';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '7a0aad34-0f2f-4c40-a77f-cee92c9550a3';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '50e4e84d-4148-4635-8372-4f2262747668';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '33f2cd33-cf38-4247-9097-75f895d1ef7a';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = 'c2e38698-24cd-4209-b560-02c225f3ff4a';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '57db749f-e69e-4d88-b9d1-66203da05da1';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '273dd749-3804-48c8-8306-078f1e7f3b3f';");
            migrationBuilder.Sql(@"DELETE FROM pages WHERE id = '84b892fc-6ca4-4c7e-8b7c-2f2f6954862f';");

            // ========================================
            // Remove Sitemap Nodes
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = 'f04a7e50-f56a-4c5a-aa82-8b1028a05eeb';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = '98c2a9bc-5576-4b90-b72c-d2cd7a3da5a5';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = '48200d8b-6b7d-47b5-931c-17033ad8a679';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = 'dda5c020-c2bd-4f1f-9d8d-447659decc15';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = '8950c6c6-7848-4a0b-b260-e8dbedf7486c';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = '8c27983c-d215-48ad-9e73-49fd4e8acdb8';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_nodes WHERE id = '3edb7097-a998-4e2e-9ba0-716f0767ce35';");

            // ========================================
            // Remove Sitemap Areas
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = '83ebdcfd-a244-4fba-9e25-f96fe27b7d0d';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = 'b7ddb30a-0d8b-4d52-a392-5cc6136fb7a4';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = 'dadd2bb1-459b-48da-a798-f2eea579c4e5';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = '9aacb1b4-c03d-44bb-8d79-554971f4a25c';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = '24028a64-748b-43a2-98ae-47514da142fe';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = 'fe9ac91f-a52f-4127-a74b-c4b335930c1d';");
            migrationBuilder.Sql(@"DELETE FROM app_sitemap_areas WHERE id = 'd99e07df-b5f3-4a01-8506-b607c3389308';");

            // ========================================
            // Remove Application
            // ========================================
            migrationBuilder.Sql(@"DELETE FROM applications WHERE id = '652ccabf-d5ad-46d8-aa67-25842537ed4c';");
        }
    }
}
