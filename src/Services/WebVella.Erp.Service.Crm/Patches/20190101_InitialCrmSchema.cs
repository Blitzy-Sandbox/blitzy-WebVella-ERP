#nullable disable

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace WebVella.Erp.Service.Crm.Patches
{
    /// <summary>
    /// Initial CRM schema migration consolidating all entity patches from:
    /// - NextPlugin.Patch20190203 (account, case, case_status, case_type, industry entities)
    /// - NextPlugin.Patch20190204 (contact, address entities, account CRM fields, CRM relations)
    /// - NextPlugin.Patch20190206 (salutation entity, field corrections — removed typo solutation_id, added salutation_id)
    ///
    /// All hard-coded GUIDs from monolith patches are preserved for data migration compatibility.
    /// Table naming follows monolith convention: rec_{entity_name} for entity tables, rel_{relation_name} for join tables.
    /// No foreign key constraints are created for cross-service references (country_id, language_id, currency_id, user refs).
    /// FK constraints are only created for CRM-internal relations (case→case_status, case→case_type, account→salutation, contact→salutation).
    /// </summary>
    [Migration("20190101000000_InitialCrmSchema")]
    public partial class InitialCrmSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // =====================================================================
            // PHASE 1: Lookup tables (no foreign key dependencies)
            // =====================================================================

            #region rec_case_status — Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27

            migrationBuilder.CreateTable(
                name: "rec_case_status",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_index = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 1.0m),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_case_status", x => x.id);
                });

            // Seed data: 9 case_status records from NextPlugin.Patch20190203
            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("4f17785b-c430-4fea-9fa9-8cfef931c60e"), true, "Open", 1.0m, false, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("c04d2a73-9fd3-4d00-b32e-9887e517f3bf"), false, "Closed - Duplicate", 103.0m, true, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("b7368bd9-ea1c-4091-8c57-26e5c8360c29"), false, "Closed - No Response", 102.0m, true, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("2aac0c08-5e84-477d-add0-5bc60057eba4"), false, "Closed - Resolved", 100.0m, true, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("61cba6d4-b175-4a89-94b6-6b700ce9adb9"), false, "Closed - Rejected", 101.0m, true, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("fe9d8d44-996a-4e8a-8448-3d7731d4f278"), false, "Re-Open", 10.0m, false, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("508d9e1b-8896-46ed-a6fd-734197bdb1c8"), false, "Wait for Customer", 50.0m, false, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("95170be2-dcd9-4399-9ac4-7ecefb67ad2d"), false, "Escalated", 52.0m, false, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_status",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_closed", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("ef18bf1e-314e-472f-887b-e348daef9676"), false, "On Hold", 40.0m, false, true, true, "", null, null });

            #endregion

            #region rec_case_type — Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad

            migrationBuilder.CreateTable(
                name: "rec_case_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_index = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 1.0m),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_case_type", x => x.id);
                });

            // Seed data: 5 case_type records from NextPlugin.Patch20190203
            migrationBuilder.InsertData(
                table: "rec_case_type",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("3298c9b3-560b-48b2-b148-997f9cbb3bec"), true, "General", 1.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_type",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("f228d073-bd09-48ed-85c7-54c6231c9182"), false, "Problem", 2.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_type",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("92b35547-f91b-492d-9c83-c29c3a4d132d"), false, "Question", 3.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_type",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("15e7adc5-a3e7-47c5-ae54-252cffe82923"), false, "Feature Request", 4.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_case_type",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("dc4b7e9f-0790-47b5-a89c-268740aded38"), false, "Duplicate", 5.0m, true, true, "", null, null });

            #endregion

            #region rec_industry — Entity ID: 2c60e662-367e-475d-9fcb-3ead55178a56

            migrationBuilder.CreateTable(
                name: "rec_industry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_index = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 1.0m),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    icon_class = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_industry", x => x.id);
                });

            // Seed data: 32 industry records from NextPlugin.Patch20190203
            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("991ac1a3-1488-4721-ba1d-e31602d2259c"), false, "Agriculture", 1.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("2dedd5cf-f7ba-4c60-a8a0-24b877254f6d"), false, "Apparel", 2.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("57387434-69f1-4412-81d5-cfc78accb136"), false, "Banking", 3.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("b3f98678-054a-42c3-8417-461a36432cbb"), false, "Biotechnology", 4.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("5fdf025f-3f0f-422b-8c01-0e836a244cb1"), false, "Chemicals", 5.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("ef119a92-0aee-455c-aca0-6dd511f94311"), false, "Communications", 6.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("7651b55b-acd6-48c1-8cb4-a23f1abf5aca"), false, "Construction", 7.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("86db7d49-31e7-4a25-a1c5-f738c02c603b"), false, "Consulting", 8.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("ea52bba9-8215-4103-a7b0-a6eb0c5e99ff"), false, "Education", 9.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("86b7d188-9595-4e38-bf00-c2c6754657f6"), false, "Electronics", 10.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("cf54ccbc-1334-49d4-a51d-159b33dbc6b4"), false, "Energy", 11.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("532204c0-ed8b-44b2-80fa-c582d38e3218"), false, "Engineering", 12.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("37714bd5-2f00-4211-a13d-bb78f5d71263"), false, "Entertainment", 13.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("c0f0ae79-5ec2-436f-ab80-07986ca7a7e0"), false, "Environmental", 14.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("30cd82e0-7392-45ba-8cf5-7346eb7af733"), false, "Finance", 15.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("068b7b08-de54-4628-bc54-b2fe614a42ba"), false, "Food & Beverage", 16.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("e91a880a-ee18-4a3d-b23c-8ea29a02b3f7"), false, "Government", 17.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("d4373f58-0427-4d6d-90dc-ceb62a11fef8"), false, "Healthcare", 18.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("9c3e18f9-e95e-4af5-b1db-60a610b3c64e"), false, "Hospitality", 19.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("b1756fdc-055e-4df3-909a-594c586495c5"), false, "Insurance", 20.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("2890c7f0-b213-41f1-9bf4-a3d93df9d727"), false, "Machinery", 21.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("a557bb08-e5f6-46aa-a848-b5faa6d3e644"), false, "Manufacturing", 22.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("b22f8247-15e7-4e4b-bbd2-8c2c62dfee09"), false, "Media", 23.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("904b50d2-ef93-442e-a5e8-92a690d0b8bd"), false, "Not for Profit", 24.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("ad4991cd-a3a1-4e4e-9046-71700e2a5bfb"), false, "Recreation", 25.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("cc7549f8-a583-4da9-875b-c81617ea6c41"), false, "Retail", 26.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("ef55d4fe-0979-49be-be8e-27c57c9cde31"), false, "Shipping", 27.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("b5b8af14-c500-40d9-9bb8-76a03e34425c"), false, "Technology", 28.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("23488f45-0108-445d-ad4b-91d2cd516298"), false, "Telecommunications", 29.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("9caa3931-75e0-43d8-b98e-674e11afae21"), false, "Transportation", 30.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("12686d8f-0a19-4721-a7f2-0ab946afc746"), false, "Utilities", 31.0m, true, true, "", null, null });

            migrationBuilder.InsertData(
                table: "rec_industry",
                columns: new[] { "id", "is_default", "label", "sort_index", "is_system", "is_enabled", "l_scope", "icon_class", "color" },
                values: new object[] { new Guid("667251fa-9bcf-4d3f-b538-6b6b3926ca53"), false, "Other", 32.0m, true, true, "", null, null });

            #endregion

            #region rec_salutation — Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def

            migrationBuilder.CreateTable(
                name: "rec_salutation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    sort_index = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 1.0m),
                    l_scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_salutation", x => x.id);
                });

            // Seed data: 5 salutation records from NextPlugin.Patch20190206
            migrationBuilder.InsertData(
                table: "rec_salutation",
                columns: new[] { "id", "is_default", "is_enabled", "is_system", "label", "sort_index", "l_scope" },
                values: new object[] { new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"), true, true, true, "Mr.", 1.0m, "" });

            migrationBuilder.InsertData(
                table: "rec_salutation",
                columns: new[] { "id", "is_default", "is_enabled", "is_system", "label", "sort_index", "l_scope" },
                values: new object[] { new Guid("0ede7d96-2d85-45fa-818b-01327d4c47a9"), false, true, true, "Ms.", 2.0m, "" });

            migrationBuilder.InsertData(
                table: "rec_salutation",
                columns: new[] { "id", "is_default", "is_enabled", "is_system", "label", "sort_index", "l_scope" },
                values: new object[] { new Guid("ab073457-ddc8-4d36-84a5-38619528b578"), false, true, true, "Mrs.", 3.0m, "" });

            migrationBuilder.InsertData(
                table: "rec_salutation",
                columns: new[] { "id", "is_default", "is_enabled", "is_system", "label", "sort_index", "l_scope" },
                values: new object[] { new Guid("5b8d0137-9ec5-4b1c-a9b0-e982ef8698c1"), false, true, true, "Dr.", 4.0m, "" });

            migrationBuilder.InsertData(
                table: "rec_salutation",
                columns: new[] { "id", "is_default", "is_enabled", "is_system", "label", "sort_index", "l_scope" },
                values: new object[] { new Guid("a74cd934-b425-4061-8f4e-a6d6b9d7adb1"), false, true, true, "Prof.", 5.0m, "" });

            #endregion

            // =====================================================================
            // PHASE 2: Main entity tables (may reference lookup tables)
            // =====================================================================

            #region rec_account — Entity ID: 2e22b50f-e444-4b62-a171-076e51246939

            migrationBuilder.CreateTable(
                name: "rec_account",
                columns: table => new
                {
                    // System PK field (from Patch20190203, system field ID: 4c0c80d0-8b01-445f-9913-0be18d9086d1)
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    // Patch20190203: name field (ID: b8be9afb-687c-411a-a274-ebe5d36a8100)
                    name = table.Column<string>(type: "text", nullable: false, defaultValue: "name"),
                    // Patch20190203: l_scope field (ID: fda3238e-52b5-48b7-82ad-558573c6e25c)
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: type field (ID: 7cab7793-1ae4-4c05-9191-4035a0d54bd1) — Select: Company/Person
                    type = table.Column<string>(type: "text", nullable: false, defaultValue: "1"),
                    // Patch20190204: website field (ID: df7114b5-49ad-400b-ae16-a6ed1daa8a0c)
                    website = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: street field (ID: 1bc1ead8-2673-4cdd-b0f3-b99d4cf4fadc)
                    street = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: region field (ID: 9c29b56d-2db2-47c6-bcf6-96cbe7187119)
                    region = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: post_code field (ID: caaaf464-67b7-47b2-afec-beec03d90e4f)
                    post_code = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: fixed_phone field (ID: f51f7451-b9f1-4a5a-a282-3d83525a9094)
                    fixed_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: mobile_phone field (ID: 01e8d8e6-457b-49c8-9194-81f06bd9f8ed)
                    mobile_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: fax_phone field (ID: 8f6bbfac-8f10-4023-b2b0-af03d22b9cef)
                    fax_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: notes field (ID: d2c7a984-c173-434f-a711-1f1efa07f0c1)
                    notes = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: last_name field (ID: c9da8e17-9511-4f2c-8576-8756f34a17b9)
                    last_name = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: first_name field (ID: 66de2df4-f42a-4bc9-817d-8960578a8302)
                    first_name = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: x_search field (ID: d8ce135d-f6c4-45b7-a543-c58e154c06df)
                    x_search = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: email field (ID: 25dcf767-2e12-4413-b096-60d37700194f)
                    email = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: city field (ID: 4e18d041-0daf-4db4-9bd9-6d5b631af0bd)
                    city = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: country_id field (ID: 76c1d754-8bf5-4a78-a2d7-bf771e1b032b) — cross-service ref to Core
                    country_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190204: tax_id field (ID: c4bbc47c-2dc0-4c24-9159-1b4a78cbf2d3)
                    tax_id = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: street_2 field (ID: 8829ff72-2910-40a8-834d-5f05c51c8d2f)
                    street_2 = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: language_id field (ID: 02b796b4-2b7a-4662-8a16-01dbffdd1ba1) — cross-service ref to Core
                    language_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190204: currency_id field (ID: c2a2a490-951d-4395-b359-0dc88ad56c11) — cross-service ref to Core
                    currency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190206: created_on field (ID: 48a33ffe-d5e4-4fa1-b74c-272733201652)
                    created_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    // Patch20190206: salutation_id field (ID: dce30f5b-7c87-450e-a60a-757f758d9f62) — FK to rec_salutation
                    // NOTE: solutation_id (typo) was created in Patch20190204 and DELETED in Patch20190206; replaced by salutation_id
                    salutation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_account", x => x.id);
                    table.ForeignKey(
                        name: "FK_rec_account_rec_salutation_salutation_id",
                        column: x => x.salutation_id,
                        principalTable: "rec_salutation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            #endregion

            #region rec_contact — Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0

            migrationBuilder.CreateTable(
                name: "rec_contact",
                columns: table => new
                {
                    // System PK field (system field ID: 859f24ec-4d3e-4597-9972-1d5a9cba918b)
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    // Patch20190204: email field (ID: ca400904-1334-48fe-884c-223df1d08545)
                    email = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: job_title field (ID: ddcc1807-6651-411d-9eed-668ee34d0c1b)
                    job_title = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: first_name field (ID: 6670c70c-c46e-4912-a70f-b1ad20816415)
                    first_name = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: last_name field (ID: 4f711d55-11a7-464a-a4c3-3b3047c6c014)
                    last_name = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: notes field (ID: 9912ff90-bc26-4879-9615-c5963a42fe22)
                    notes = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: fixed_phone field (ID: 0f947ba0-ccac-40c4-9d31-5e5f5be953ce)
                    fixed_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: mobile_phone field (ID: 519bd797-1dc7-4aef-b1ed-f27442f855ef)
                    mobile_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: fax_phone field (ID: 0475b344-8f8e-464c-a182-9c2beae105f3)
                    fax_phone = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: city field (ID: acc25b72-6e17-437f-bfaf-f514b0a7406f)
                    city = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: country_id field (ID: 08a67742-21ef-4ecb-8872-54ac18b50bdc) — cross-service ref to Core
                    country_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190204: region field (ID: f5cab626-c215-4922-be4f-8931d0cf0b66)
                    region = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: street field (ID: 1147a14a-d9ae-4c88-8441-80f668676b1c)
                    street = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: street_2 field (ID: 2b1532c0-528c-4dfb-b40a-3d75ef1491fc)
                    street_2 = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: post_code field (ID: c3433c76-dee9-4dce-94a0-ea5f03527ee6)
                    post_code = table.Column<string>(type: "text", nullable: true),
                    // Patch20190206: created_on field
                    created_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    // Patch20190206: photo field — Image type stored as path/URL
                    photo = table.Column<string>(type: "text", nullable: true),
                    // Patch20190206: x_search field — MultiLineText for full-text search indexing
                    x_search = table.Column<string>(type: "text", nullable: true),
                    // Patch20190206: salutation_id field — FK to rec_salutation
                    // NOTE: solutation_id (typo) was created in Patch20190204 and DELETED in Patch20190206; replaced by salutation_id
                    salutation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190204: l_scope field (inferred from entity pattern)
                    l_scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_contact", x => x.id);
                    table.ForeignKey(
                        name: "FK_rec_contact_rec_salutation_salutation_id",
                        column: x => x.salutation_id,
                        principalTable: "rec_salutation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            #endregion

            #region rec_case — Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c

            migrationBuilder.CreateTable(
                name: "rec_case",
                columns: table => new
                {
                    // System PK field (system field ID: 5f50a281-8106-4b21-bb14-78ba7cf8ba37)
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    // Patch20190203: account_id field (ID: 829fefbc-3578-4311-881c-33597d236830) — denormalized cross-service ref
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190203: created_on field (ID: 104ef526-773d-464a-98cd-774d184cc7de)
                    created_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    // Patch20190203: created_by field (ID: c3d1aeb5-0d96-4be0-aa9e-d7732ca68709) — cross-service ref to user in Core
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    // Patch20190203: owner_id field (ID: 3c25fb36-8d33-4a90-bd60-7a9bf401b547) — cross-service ref to user in Core
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190203: description field (ID: b8ac2f8c-1f24-4452-ad47-e7f3cf254ff4) — InputHtmlField
                    description = table.Column<string>(type: "text", nullable: true),
                    // Patch20190203: subject field (ID: 8f5477aa-0fc6-4c97-9192-b9dadadaf497)
                    subject = table.Column<string>(type: "text", nullable: false, defaultValue: "subject"),
                    // Patch20190203: number field (ID: 19648468-893b-49f9-b8bd-b84add0c50f5) — AutoNumber, unique
                    number = table.Column<string>(type: "text", nullable: true),
                    // Patch20190203: closed_on field (ID: ac852183-e438-4c84-aaa3-dc12a0f2ad8e)
                    closed_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    // Patch20190203: l_scope field (ID: b8af3f7a-78a4-445c-ad28-b7eea1d9eff5)
                    l_scope = table.Column<string>(type: "text", nullable: true),
                    // Patch20190203: priority field (ID: 1dbe204d-3771-4f56-a2f5-bff0cf1831b4) — Select: high/medium/low
                    priority = table.Column<string>(type: "text", nullable: false, defaultValue: "low"),
                    // Patch20190203: status_id field (ID: 05b97041-7a65-4d27-8c06-fc154d2fcbf5) — FK to rec_case_status
                    status_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190203: type_id field (ID: 0b1f1244-6090-41e7-9684-53d2968bb33a) — FK to rec_case_type
                    type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190206: x_search field — MultiLineText for full-text search indexing
                    x_search = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_case", x => x.id);
                    table.ForeignKey(
                        name: "FK_rec_case_rec_case_status_status_id",
                        column: x => x.status_id,
                        principalTable: "rec_case_status",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_rec_case_rec_case_type_type_id",
                        column: x => x.type_id,
                        principalTable: "rec_case_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            #endregion

            #region rec_address — Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0

            migrationBuilder.CreateTable(
                name: "rec_address",
                columns: table => new
                {
                    // System PK field (system field ID: 158c33cc-f7b2-4b0a-aeb6-ce5e908f6c5d)
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    // Patch20190204: street field (ID: 79e7a689-6407-4a03-8580-5bdb20e2337d)
                    street = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: street_2 field (ID: 3aeb73d9-8879-4f25-93e9-0b22944a5bba)
                    street_2 = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: city field (ID: 6b8150d5-ea81-4a74-b35a-b6c888665fe5)
                    city = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: region field (ID: 6225169e-fcde-4c66-9066-d08bbe9a7b1b)
                    region = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: country_id field (ID: c40192ea-c81c-4140-9c7b-6134184f942c) — cross-service ref to Core
                    country_id = table.Column<Guid>(type: "uuid", nullable: true),
                    // Patch20190204: notes field (ID: a977b2af-78ea-4df0-97dc-652d82cee2df)
                    notes = table.Column<string>(type: "text", nullable: true),
                    // Patch20190204: name field (ID: 487d6795-6cec-4598-bbeb-094bcbeadcf6)
                    name = table.Column<string>(type: "text", nullable: true),
                    // AAP specified: post_code and l_scope columns for address completeness
                    post_code = table.Column<string>(type: "text", nullable: true),
                    l_scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rec_address", x => x.id);
                });

            #endregion

            // =====================================================================
            // PHASE 3: ManyToMany join/relation tables
            // =====================================================================

            #region rel_account_nn_contact — Relation ID: dd211c99-5415-4195-923a-cb5a56e5d544

            migrationBuilder.CreateTable(
                name: "rel_account_nn_contact",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_account_nn_contact", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_account_nn_contact_rec_account_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_account_nn_contact_rec_contact_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_contact",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            #endregion

            #region rel_account_nn_case — Relation ID: 3690c12e-40e1-4e8f-a0a8-27221c686b43

            migrationBuilder.CreateTable(
                name: "rel_account_nn_case",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_account_nn_case", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_account_nn_case_rec_account_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_account_nn_case_rec_case_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_case",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            #endregion

            #region rel_address_nn_account — Relation ID: dcf76eb5-16cf-466d-b760-c0d8ae57da94

            migrationBuilder.CreateTable(
                name: "rel_address_nn_account",
                columns: table => new
                {
                    origin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rel_address_nn_account", x => new { x.origin_id, x.target_id });
                    table.ForeignKey(
                        name: "FK_rel_address_nn_account_rec_address_origin_id",
                        column: x => x.origin_id,
                        principalTable: "rec_address",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_rel_address_nn_account_rec_account_target_id",
                        column: x => x.target_id,
                        principalTable: "rec_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            #endregion

            // =====================================================================
            // PHASE 4: Indexes on foreign key and search columns
            // =====================================================================

            #region Indexes

            // rec_account indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_account_salutation_id",
                table: "rec_account",
                column: "salutation_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_account_x_search",
                table: "rec_account",
                column: "x_search");

            migrationBuilder.CreateIndex(
                name: "IX_rec_account_type",
                table: "rec_account",
                column: "type");

            // rec_contact indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_contact_salutation_id",
                table: "rec_contact",
                column: "salutation_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_contact_x_search",
                table: "rec_contact",
                column: "x_search");

            // rec_case indexes
            migrationBuilder.CreateIndex(
                name: "IX_rec_case_status_id",
                table: "rec_case",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_case_type_id",
                table: "rec_case",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_case_account_id",
                table: "rec_case",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_rec_case_x_search",
                table: "rec_case",
                column: "x_search");

            // Join table indexes (non-PK column indexes for reverse lookups)
            migrationBuilder.CreateIndex(
                name: "IX_rel_account_nn_contact_target_id",
                table: "rel_account_nn_contact",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_account_nn_case_target_id",
                table: "rel_account_nn_case",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_rel_address_nn_account_target_id",
                table: "rel_address_nn_account",
                column: "target_id");

            #endregion

            // =====================================================================
            // PHASE 5: SQL comments with preserved entity GUIDs for data migration
            // =====================================================================

            #region SQL Comments — Entity GUIDs for data migration reference

            migrationBuilder.Sql("COMMENT ON TABLE rec_case_status IS 'Entity ID: 960afdc1-cd78-41ab-8135-816f7f7b8a27';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_case_type IS 'Entity ID: 0dfeba58-40bb-4205-a539-c16d5c0885ad';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_industry IS 'Entity ID: 2c60e662-367e-475d-9fcb-3ead55178a56';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_salutation IS 'Entity ID: 690dc799-e732-4d17-80d8-0f761bc33def';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_account IS 'Entity ID: 2e22b50f-e444-4b62-a171-076e51246939';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_contact IS 'Entity ID: 39e1dd9b-827f-464d-95ea-507ade81cbd0';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_case IS 'Entity ID: 0ebb3981-7443-45c8-ab38-db0709daf58c';");
            migrationBuilder.Sql("COMMENT ON TABLE rec_address IS 'Entity ID: 34a126ba-1dee-4099-a1c1-a24e70eb10f0';");
            migrationBuilder.Sql("COMMENT ON TABLE rel_account_nn_contact IS 'Relation ID: dd211c99-5415-4195-923a-cb5a56e5d544';");
            migrationBuilder.Sql("COMMENT ON TABLE rel_account_nn_case IS 'Relation ID: 3690c12e-40e1-4e8f-a0a8-27221c686b43';");
            migrationBuilder.Sql("COMMENT ON TABLE rel_address_nn_account IS 'Relation ID: dcf76eb5-16cf-466d-b760-c0d8ae57da94';");

            #endregion
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop tables in reverse dependency order

            // Join/relation tables first
            migrationBuilder.DropTable(name: "rel_address_nn_account");
            migrationBuilder.DropTable(name: "rel_account_nn_case");
            migrationBuilder.DropTable(name: "rel_account_nn_contact");

            // Main entity tables (depend on lookup tables)
            migrationBuilder.DropTable(name: "rec_case");
            migrationBuilder.DropTable(name: "rec_address");
            migrationBuilder.DropTable(name: "rec_contact");
            migrationBuilder.DropTable(name: "rec_account");

            // Lookup tables last (referenced by main tables)
            migrationBuilder.DropTable(name: "rec_salutation");
            migrationBuilder.DropTable(name: "rec_industry");
            migrationBuilder.DropTable(name: "rec_case_type");
            migrationBuilder.DropTable(name: "rec_case_status");
        }
    }
}
