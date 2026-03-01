# Technical Specification

# 0. Agent Action Plan

## 0.1 Intent Clarification


### 0.1.1 Core Refactoring Objective

Based on the prompt, the Blitzy platform understands that the refactoring objective is to perform a **complete architectural rewrite** of the WebVella ERP platform — decomposing a monolithic ASP.NET Core MVC 9.0 application (targeting `net10.0`) into a serverless microservices architecture on AWS, with all development and testing performed exclusively against LocalStack (no live AWS account required).

- **Refactoring type:** Tech stack migration + Architectural decomposition (Monolith → Serverless Microservices)
- **Target repository:** New repository (Nx monorepo replacing the current `.sln`-based structure)
- **Source system:** WebVella ERP v1.7.7 — a metadata-driven, plugin-based modular monolith running ASP.NET Core with Razor Pages, jQuery, StencilJS components, and a single PostgreSQL database
- **Target system:** 10 bounded-context Lambda-backed services (`.NET 9 Native AOT` + `Node.js 22`) fronted by HTTP API Gateway v2, with a React 19 SPA (Vite 6) served from S3 static hosting

**Refactoring Goals (enhanced clarity):**

- **Bounded Context Extraction** — Decompose the monolith's tightly coupled subsystems (`WebVella.Erp/Api/`, `WebVella.Erp/Database/`, `WebVella.Erp.Web/`, and 6 plugin projects) into 10 independently deployable, independently scalable Lambda-backed services, each owning its own datastore
- **Frontend Replatforming** — Replace the server-rendered Razor Pages UI (`WebVella.Erp.Web/Pages/`, `WebVella.Erp.Web/Components/` with 50+ ViewComponents), jQuery DOM manipulation, and StencilJS web components (`wwwroot/js/` bundles) with a pure React 19 SPA built with Vite 6 and deployed as static assets to S3
- **Database-Per-Service Isolation** — Migrate from a single PostgreSQL database (with 20+ `rec_*` dynamic tables, `entities` JSON doc store, `entity_relations`, `app*` sitemap tables, `jobs`, `files`, `system_log`, `plugin_data`) to per-service datastores using DynamoDB (default) and RDS PostgreSQL (for ACID-critical domains like Invoicing)
- **Infrastructure as Code** — Define 100% of AWS resources via CDK 2.x (TypeScript), deployable against both LocalStack (`cdklocal --context localstack=true`) and production AWS (`cdk deploy`) using a single codebase with context-flag toggling
- **Async Event-Driven Communication** — Replace in-process hook invocations (`HookManager`, `RecordHookManager`) and PostgreSQL `LISTEN/NOTIFY` notifications with SNS topics for domain events and SQS queues for consumer decoupling
- **Authentication Migration** — Replace cookie-based ASP.NET Core authentication + custom JWT middleware (`JwtMiddleware.cs`, `AuthService.cs`) with AWS Cognito user pools and HTTP API Gateway native JWT authorizer (with custom Lambda authorizer fallback for LocalStack)

### 0.1.2 Technical Interpretation

This refactoring translates to the following technical transformation strategy:

**Current Architecture → Target Architecture Mapping:**

| Dimension | Current (Monolith) | Target (Serverless) |
|-----------|-------------------|---------------------|
| Runtime | Single ASP.NET Core 10 process | 10+ Lambda functions (.NET 9 AOT / Node.js 22) |
| Frontend | Server-rendered Razor Pages + jQuery + StencilJS | React 19 SPA (Vite 6) on S3 |
| API Layer | `WebApiController.cs` (single controller, 100+ endpoints) | HTTP API Gateway v2 → per-domain Lambda handlers |
| Database | Single PostgreSQL instance (Npgsql 9.0.4) | DynamoDB (default) + RDS PostgreSQL (ACID domains) |
| Authentication | Cookie auth + JWT middleware | Cognito + API Gateway JWT authorizer |
| Background Jobs | `JobManager`/`JobPool` (in-process, 20-thread pool) | Step Functions Local + SQS-triggered Lambdas |
| Notifications | PostgreSQL LISTEN/NOTIFY | SNS/SQS event bus |
| File Storage | PostgreSQL LO / filesystem / Storage.Net | S3 (via Lambda) |
| Plugin System | Reflection-based `ErpPlugin` discovery | Plugin/Extension microservice |
| Hooks | Synchronous in-process `HookManager` | Domain events via SNS/SQS |
| EQL Engine | In-process Irony grammar → PostgreSQL SQL | Per-service query adapters (DynamoDB queries / RDS SQL) |
| IaC | None (manual deployment) | CDK 2.x with `cdklocal` dual-target |
| Monorepo | `.sln` Visual Studio solution | Nx workspace |
| CSS/Styling | Bootstrap 4 + custom CSS | Tailwind CSS 4.x |
| State Management | Server-side session + `ErpRequestContext` | TanStack Query 5 (server) + Zustand 5 (client) |

**Transformation Rules:**
- Every public API endpoint exposed by `WebApiController.cs` must map to exactly one bounded-context Lambda handler
- Every `ErpPlugin` subclass (`SdkPlugin`, `CrmPlugin`, `MailPlugin`, `ProjectPlugin`, `NextPlugin`, `MicrosoftCDMPlugin`) maps to one or more bounded-context services
- Every `RecordManager` CRUD operation translates to a domain-specific service with its own datastore
- All cross-service communication must go through SNS/SQS — zero direct database access across boundaries
- The React SPA must reproduce every user-facing workflow currently rendered by Razor Pages + ViewComponents

**Implicit Requirements Surfaced:**
- The EQL (Entity Query Language) engine (`WebVella.Erp/Eql/`) must be re-implemented as per-service query adapters since the target architecture uses heterogeneous datastores (DynamoDB + PostgreSQL)
- The dynamic entity/field definition system (`EntityManager`, `DbEntityRepository`) must be decomposed — the Entity Management service owns metadata, while each domain service owns its records
- The `BaseErpPageModel` request context pipeline (app/area/node/page resolution) must be replicated as React Router route configuration with corresponding API calls
- The hook system's synchronous pre/post CRUD interception must be preserved as domain event publishing (post-hooks) and API-level validation (pre-hooks)
- Migration of existing user credentials from MD5-hashed passwords (`SecurityManager`) to Cognito user pool requires a custom migration Lambda


## 0.2 Source Analysis


### 0.2.1 Comprehensive Source File Discovery

The WebVella ERP monolith is organized as a Visual Studio 2022 solution (`WebVella.ERP3.sln`) containing 15+ projects. Every file in this repository is a source for the rewrite — business logic extraction, API contract analysis, and UI workflow mapping.

**Current Monolith Structure:**

```
WebVella.ERP3.sln
├── WebVella.Erp/                          (Core Engine - Class Library, net10.0)
│   ├── Api/                               (Entity/Record/Security managers, DataSource, Cache)
│   │   ├── Cache.cs                       (IMemoryCache wrapper for entities/relations)
│   │   ├── DataSourceManager.cs           (Code + DB datasource registry and execution)
│   │   ├── Definitions.cs                 (SystemIds, enums, CurrencyType)
│   │   ├── EntityManager.cs               (Entity/field CRUD, metadata validation)
│   │   ├── EntityRelationManager.cs       (Relation CRUD, immutability rules)
│   │   ├── ImportExportManager.cs          (CSV import/export pipelines)
│   │   ├── RecordManager.cs               (Record CRUD, hooks, relation processing)
│   │   ├── SearchManager.cs               (PostgreSQL FTS search index)
│   │   ├── SecurityContext.cs             (AsyncLocal user scoping, permission checks)
│   │   ├── SecurityManager.cs             (User/role CRUD, credential validation - MD5)
│   │   └── Models/                        (Entity, Field, Relation, Record DTOs - 20+ field types)
│   ├── Database/                          (PostgreSQL persistence layer)
│   │   ├── DbContext.cs                   (Ambient context, connection/transaction management)
│   │   ├── DbConnection.cs               (Npgsql wrapper, savepoints, advisory locks)
│   │   ├── DbRepository.cs               (DDL/DML helpers, table/column/index operations)
│   │   ├── DbEntityRepository.cs          (Entity JSON doc store, rec_* table management)
│   │   ├── DbRelationRepository.cs        (Relation docs, FK/join table management)
│   │   ├── DbRecordRepository.cs          (Dynamic record CRUD, query → SQL translation)
│   │   ├── DbFileRepository.cs            (File lifecycle: LO/filesystem/blob backends)
│   │   ├── DBTypeConverter.cs             (Field type → PostgreSQL type mapping)
│   │   ├── DbDataSourceRepository.cs      (data_source table CRUD)
│   │   ├── DbSystemSettings.cs            (system_settings model + repository)
│   │   ├── AutoMapper/                    (DB entity ↔ API model mappings)
│   │   └── FieldTypes/                    (Per-type DB field implementations)
│   ├── Eql/                               (Entity Query Language subsystem)
│   │   ├── EqlGrammar.cs                  (Irony grammar: SELECT/FROM/WHERE/ORDER/PAGE)
│   │   ├── EqlAbstractTree.cs             (AST nodes)
│   │   ├── EqlBuilder.cs                  (Parse → AST → validate → hooks)
│   │   ├── EqlBuilder.Sql.cs              (AST → PostgreSQL SQL with JSON results)
│   │   ├── EqlCommand.cs                  (Execute: parameter bind, NpgsqlDataAdapter)
│   │   └── EqlParameter.cs, EqlSettings.cs, EqlFieldMeta.cs, EqlError.cs, EqlException.cs
│   ├── Hooks/                             (Event-driven extensibility)
│   │   ├── HookManager.cs                 (Reflection-based discovery/registration)
│   │   ├── RecordHookManager.cs           (Pre/post CRUD hook orchestration)
│   │   └── IErp*Hook.cs                   (12 hook interface contracts)
│   ├── Jobs/                              (Background processing)
│   │   ├── JobManager.cs                  (Singleton coordinator, job type registry)
│   │   ├── JobPool.cs                     (Bounded 20-thread executor)
│   │   ├── JobDataService.cs              (PostgreSQL-backed job/schedule persistence)
│   │   ├── SheduleManager.cs              (Schedule plan orchestrator)
│   │   └── ErpBackgroundServices.cs       (Generic Host BackgroundService)
│   ├── Notifications/                     (PostgreSQL LISTEN/NOTIFY pub/sub)
│   ├── Recurrence/                        (iCal.Net recurrence processing)
│   ├── Fts/                               (Bulgarian full-text analysis)
│   ├── Diagnostics/                       (system_log DB logging)
│   ├── Exceptions/                        (ValidationException, StorageException)
│   ├── Utilities/                         (Crypto, DateTime, JSON, text helpers)
│   ├── ERPService.cs                      (Bootstrap orchestrator)
│   ├── ErpPlugin.cs                       (Abstract plugin base)
│   └── ErpSettings.cs                     (Global static config binder)
│
├── WebVella.Erp.Web/                      (Web/UI Layer - Razor Class Library, net10.0)
│   ├── Controllers/
│   │   ├── ApiControllerBase.cs           (Auth default, JSON response helpers)
│   │   └── WebApiController.cs            (Primary API surface: 100+ endpoints)
│   ├── Pages/                             (Razor Pages: Index, App, Record CRUD, login/logout)
│   │   ├── Index.cshtml[.cs]              (Home page)
│   │   ├── ApplicationHome.cshtml[.cs]    (App home)
│   │   ├── ApplicationNode.cshtml[.cs]    (App node)
│   │   ├── RecordCreate.cshtml[.cs]       (Record creation)
│   │   ├── RecordDetails.cshtml[.cs]      (Record detail + delete)
│   │   ├── RecordList.cshtml[.cs]         (Record listing)
│   │   ├── RecordManage.cshtml[.cs]       (Record editing)
│   │   ├── RecordRelatedRecord*.cshtml[.cs] (Related record CRUD)
│   │   ├── login.cshtml[.cs]              (Authentication)
│   │   ├── _AppMaster.cshtml              (Main layout chrome)
│   │   └── _SystemMaster.cshtml           (System layout)
│   ├── Components/                        (50+ ViewComponents)
│   │   ├── PcFieldBase/                   (Shared field component base)
│   │   ├── PcField*/                      (25+ field type components)
│   │   ├── PcRow/, PcGrid/, PcForm/       (Layout components)
│   │   ├── PcSection/, PcTabNav/, PcModal/ (Container components)
│   │   ├── PcButton/, PcChart/, PcDrawer/ (Widget components)
│   │   ├── Nav/, SiteMenu/, ApplicationMenu/ (Navigation chrome)
│   │   └── RenderHook/                    (Dynamic ViewComponent rendering pipeline)
│   ├── Services/                          (18 service classes)
│   │   ├── AuthService.cs                 (Cookie + JWT auth)
│   │   ├── AppService.cs                  (App/sitemap lifecycle)
│   │   ├── PageService.cs                 (Page CRUD, body tree, caching)
│   │   ├── UserService.cs, UserFileService.cs, UserPreferencies.cs
│   │   ├── RenderService.cs, ThemeService.cs, CodeEvalService.cs
│   │   └── LogService.cs, MailService.cs, MetaService.cs
│   ├── Middleware/
│   │   ├── ErpMiddleware.cs               (Per-request DB context + security scope)
│   │   ├── JwtMiddleware.cs               (JWT token validation)
│   │   ├── ErpErrorHandlingMiddleware.cs  (Global exception capture)
│   │   └── SecuritityCircuitHandler.cs    (Blazor circuit lifecycle)
│   ├── Repositories/                      (8 Npgsql repositories: App, Page, Sitemap*, etc.)
│   ├── Models/                            (40+ DTOs: App, Page, Component, Theme, JWT, etc.)
│   ├── TagHelpers/                        (wv-* Razor tag helpers)
│   ├── Hooks/                             (Page lifecycle hook interfaces)
│   ├── wwwroot/                           (Static assets: JS, CSS, images)
│   └── ErpMvcExtensions.cs               (AddErp/UseErp startup wiring)
│
├── WebVella.Erp.Plugins.SDK/             (SDK Admin Console plugin)
│   ├── SdkPlugin.cs + SdkPlugin.*.cs     (Plugin entry + 5 dated patch files)
│   ├── Controllers/AdminController.cs     (api/v3.0/p/sdk/* endpoints)
│   ├── Services/CodeGenService.cs, LogService.cs
│   ├── Pages/                             (Admin UI: entity/page/datasource/role/user/job editors)
│   ├── Components/                        (Sitemap form + body-top includes)
│   └── wwwroot/                           (Stencil bundles: datasource-manage, sitemap-manager, pb-manager)
│
├── WebVella.Erp.Plugins.Next/            (Entity provisioning + search indexing)
│   ├── NextPlugin.cs + NextPlugin.*.cs    (5 dated patch files creating account/contact/task entities)
│   ├── Configuration.cs                   (Search index field definitions)
│   ├── Services/SearchService.cs          (x_search field regeneration)
│   └── Hooks/Api/                         (Post-create/update hooks for 4 entities)
│
├── WebVella.Erp.Plugins.Project/         (Project Management plugin)
│   ├── ProjectPlugin.cs + 9 patch files   (Extensive page/datasource seeding)
│   ├── Controllers/                       (api/v3.0/p/project/* endpoints)
│   ├── Services/                          (Task, Timelog, Comment, Feed, Reporting services)
│   ├── Components/                        (Dashboard widgets, feed, timelog, recurrence)
│   ├── Hooks/, Jobs/, Datasource/
│   └── wwwroot/                           (Stencil bundles: feed, post-list, timelog-list, recurrence)
│
├── WebVella.Erp.Plugins.Crm/            (CRM plugin - skeleton)
│   ├── CrmPlugin.cs + CrmPlugin._.cs     (Plugin entry + patch runner)
│   └── Model/PluginSettings.cs
│
├── WebVella.Erp.Plugins.Mail/           (Email plugin)
│   ├── MailPlugin.cs + 7 patch files      (email/smtp_service entity creation)
│   ├── Api/                               (DTOs, enums, AutoMapper)
│   ├── Services/                          (SMTP engine: validation, send, queue processing)
│   ├── Hooks/                             (SMTP record invariants, UI actions)
│   └── Jobs/                              (Scheduled SMTP queue processor)
│
├── WebVella.Erp.Plugins.MicrosoftCDM/   (MicrosoftCDM plugin - skeleton)
│   ├── MicrosoftCDMPlugin.cs + patch runner
│   └── Model/PluginSettings.cs
│
├── WebVella.Erp.Site/                    (Primary host application)
│   ├── Program.cs                         (WebHost builder)
│   ├── Startup.cs                         (Full DI/middleware composition)
│   ├── Config.json                        (DB connection, JWT, feature flags)
│   └── web.config                         (IIS hosting bridge)
│
├── WebVella.Erp.Site.Sdk/               (SDK-only site host variant)
├── WebVella.Erp.Site.Next/              (Next-plugin site host)
├── WebVella.Erp.Site.Project/           (Project-plugin site host)
├── WebVella.Erp.Site.Mail/              (Mail-plugin site host)
├── WebVella.Erp.Site.Crm/              (CRM-plugin site host)
├── WebVella.Erp.Site.MicrosoftCDM/     (CDM-plugin site host)
│
├── WebVella.Erp.WebAssembly/            (Blazor WASM structure)
│   ├── Client/                           (net10.0 Blazor WASM app)
│   ├── Server/                           (net7.0 hosting server)
│   └── Shared/                           (net7.0 shared library)
│
├── WebVella.Erp.ConsoleApp/             (Console harness for ERP operations)
├── docs/                                 (Developer documentation)
├── global.json                           (SDK version: commented out 7.0.103)
└── create-nuget-pkgs.bat                (NuGet packaging script)
```

### 0.2.2 Source File Inventory by Bounded Context

**Identity & Access Management Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp/Api/SecurityContext.cs` | AsyncLocal user scope, permission checks |
| `WebVella.Erp/Api/SecurityManager.cs` | User/role CRUD, MD5 credential validation |
| `WebVella.Erp/Api/Definitions.cs` | `SystemIds` (system user/role GUIDs), `EntityPermission` enum |
| `WebVella.Erp.Web/Services/AuthService.cs` | Cookie + JWT auth, token issuance/validation |
| `WebVella.Erp.Web/Middleware/JwtMiddleware.cs` | Bearer token extraction and validation |
| `WebVella.Erp.Web/Middleware/ErpMiddleware.cs` | Per-request security scope binding |
| `WebVella.Erp.Web/Models/JwtTokenModels.cs` | Login/token DTOs |
| `WebVella.Erp.Web/Pages/login.cshtml[.cs]` | Login page + authentication flow |
| `WebVella.Erp.Web/Pages/logout.cshtml[.cs]` | Logout flow |

**Entity Management Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp/Api/EntityManager.cs` | Entity/field metadata CRUD |
| `WebVella.Erp/Api/EntityRelationManager.cs` | Relation metadata CRUD |
| `WebVella.Erp/Api/RecordManager.cs` | Record CRUD, hook execution, field processing |
| `WebVella.Erp/Api/DataSourceManager.cs` | Datasource registry + execution |
| `WebVella.Erp/Api/Cache.cs` | Entity/relation metadata caching |
| `WebVella.Erp/Database/DbEntityRepository.cs` | Entity JSON docs, `rec_*` table DDL |
| `WebVella.Erp/Database/DbRelationRepository.cs` | Relation docs, FK/join tables |
| `WebVella.Erp/Database/DbRecordRepository.cs` | Dynamic record queries + SQL |
| `WebVella.Erp/Database/DbRepository.cs` | Schema DDL helpers |
| `WebVella.Erp/Eql/*` | Complete EQL engine (13 files) |
| `WebVella.Erp/Hooks/*` | Hook system (21 files) |
| `WebVella.Erp/Api/Models/*` | Entity, Field (20+ types), Relation, Record models |

**CRM / Contacts Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp.Plugins.Crm/CrmPlugin.cs` | CRM plugin entry |
| `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | `account`, `contact`, `address` entity creation |
| `WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs` | `salutation` entity, contact fields |
| `WebVella.Erp.Plugins.Next/Hooks/Api/*` | Post-CRUD hooks for account, contact |
| `WebVella.Erp.Plugins.Next/Services/SearchService.cs` | CRM entity search indexing |
| `WebVella.Erp.Plugins.Next/Configuration.cs` | Search index field sets |

**Project Management / Inventory Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp.Plugins.Project/ProjectPlugin.cs` + 9 patches | Full project/task schema seeding |
| `WebVella.Erp.Plugins.Project/Services/*` | TaskService, TimelogService, CommentService, FeedService, ReportingService |
| `WebVella.Erp.Plugins.Project/Controllers/*` | Project API endpoints |
| `WebVella.Erp.Plugins.Project/Components/*` | Dashboard widgets, timesheets, charts |
| `WebVella.Erp.Plugins.Project/Hooks/*` | Task/timelog lifecycle hooks |
| `WebVella.Erp.Plugins.Project/Jobs/*` | `StartTasksOnStartDate` scheduled job |

**Notifications / Email Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp.Plugins.Mail/MailPlugin.cs` + 7 patches | email/smtp_service entities |
| `WebVella.Erp.Plugins.Mail/Services/*` | SMTP engine: send, queue, validation |
| `WebVella.Erp.Plugins.Mail/Hooks/*` | SMTP record invariants |
| `WebVella.Erp.Plugins.Mail/Jobs/*` | Queue processor scheduled job |
| `WebVella.Erp/Notifications/*` | PostgreSQL LISTEN/NOTIFY subsystem |

**File Management Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp/Database/DbFileRepository.cs` | File CRUD: LO/filesystem/blob backends |
| `WebVella.Erp/Database/DbFile.cs` | File metadata model |
| `WebVella.Erp.Web/Services/UserFileService.cs` | User file upload/finalization |
| `WebVella.Erp.Web/Services/FileService.cs` | Embedded resource utilities |

**Workflow / Jobs Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp/Jobs/JobManager.cs` | Job type registry, dispatcher loops |
| `WebVella.Erp/Jobs/JobPool.cs` | 20-thread bounded executor |
| `WebVella.Erp/Jobs/JobDataService.cs` | PostgreSQL job/schedule persistence |
| `WebVella.Erp/Jobs/SheduleManager.cs` | Schedule plan CRUD + trigger |
| `WebVella.Erp/Recurrence/*` | iCal.Net recurrence processing |

**SDK / Plugin System Sources:**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp/ErpPlugin.cs` | Abstract plugin base with JSON metadata persistence |
| `WebVella.Erp/IErpService.cs` | Plugin initialization contract |
| `WebVella.Erp.Plugins.SDK/SdkPlugin.cs` + patches | SDK admin console |
| `WebVella.Erp.Plugins.SDK/Services/CodeGenService.cs` | Diff-based C# migration code generation |
| `WebVella.Erp.Plugins.SDK/Controllers/AdminController.cs` | SDK admin API |
| `WebVella.Erp.Plugins.MicrosoftCDM/*` | MicrosoftCDM plugin (skeleton) |

**Frontend/UI Sources (all to be replaced by React SPA):**

| Source File | Key Content |
|-------------|------------|
| `WebVella.Erp.Web/Pages/*.cshtml[.cs]` | 16 Razor Pages (routes + page models) |
| `WebVella.Erp.Web/Components/Pc*/*` | 50+ Page Builder components |
| `WebVella.Erp.Web/Components/Nav/`, `SiteMenu/`, etc. | Navigation chrome |
| `WebVella.Erp.Web/TagHelpers/*` | `wv-*` custom Razor tag helpers |
| `WebVella.Erp.Web/Models/Theme.cs` | Theme palette model |
| `WebVella.Erp.Web/wwwroot/*` | Static JS/CSS/images |
| `WebVella.Erp.Plugins.SDK/wwwroot/*` | Stencil bundles (datasource, sitemap, pb-manager) |
| `WebVella.Erp.Plugins.Project/wwwroot/*` | Stencil bundles (feed, post-list, timelog, recurrence) |


## 0.3 Scope Boundaries


### 0.3.1 Exhaustively In Scope

**Backend Service Decomposition:**
- `WebVella.Erp/**/*.cs` — All core engine source files (API, Database, EQL, Hooks, Jobs, Notifications, Recurrence, Diagnostics, Utilities)
- `WebVella.Erp.Plugins.SDK/**/*.cs` — SDK/admin plugin logic, controllers, services, pages
- `WebVella.Erp.Plugins.Next/**/*.cs` — Entity provisioning patches, search service, hooks
- `WebVella.Erp.Plugins.Project/**/*.cs` — Project management services, controllers, hooks, jobs
- `WebVella.Erp.Plugins.Crm/**/*.cs` — CRM plugin skeleton and patch framework
- `WebVella.Erp.Plugins.Mail/**/*.cs` — Email subsystem services, SMTP engine, jobs
- `WebVella.Erp.Plugins.MicrosoftCDM/**/*.cs` — CDM plugin skeleton
- `WebVella.Erp.Web/Controllers/*.cs` — API controller endpoints (to be remapped to Lambda handlers)
- `WebVella.Erp.Web/Services/*.cs` — All 18 service classes (business logic extraction)
- `WebVella.Erp.Web/Middleware/*.cs` — Auth/error/debug middleware (to be replaced by API Gateway + Lambda)
- `WebVella.Erp.Web/Repositories/*.cs` — 8 Npgsql repositories (to be replaced by per-service data access)
- `WebVella.Erp.Web/Hooks/*.cs` — Page lifecycle hook interfaces
- `WebVella.Erp.Site/Startup.cs` — DI composition and middleware pipeline (reference for service wiring)
- `WebVella.Erp.Site/Config.json` — Configuration schema (to be migrated to SSM Parameter Store)

**Frontend Complete Rewrite:**
- `WebVella.Erp.Web/Pages/**/*.cshtml` — All Razor Pages (route structure extraction)
- `WebVella.Erp.Web/Pages/**/*.cshtml.cs` — All PageModel classes (workflow logic extraction)
- `WebVella.Erp.Web/Components/**/*.cshtml` — All ViewComponent views (UI pattern extraction)
- `WebVella.Erp.Web/Components/**/*.cs` — All ViewComponent logic (state management extraction)
- `WebVella.Erp.Web/Components/**/service.js` — Page Builder client logic (React component logic)
- `WebVella.Erp.Web/TagHelpers/**/*.cs` — Tag helper logic (React component props extraction)
- `WebVella.Erp.Web/Models/*.cs` — All 40+ DTOs (TypeScript interface generation)
- `WebVella.Erp.Web/wwwroot/**/*` — All static assets (CSS → Tailwind, JS → React)
- `WebVella.Erp.Plugins.SDK/wwwroot/**/*` — Stencil web components (React component replacement)
- `WebVella.Erp.Plugins.Project/wwwroot/**/*` — Stencil web components (React component replacement)
- `WebVella.Erp.Plugins.Project/Files/*.js` — jQuery helpers (React hook replacement)

**Infrastructure as Code:**
- CDK stack definitions for all AWS resources (Lambda, API Gateway, DynamoDB, RDS, S3, SQS, SNS, Cognito, SSM, Step Functions, CloudWatch, IAM)
- `docker-compose.yml` for LocalStack Pro + Step Functions Local sidecar
- GitHub Actions CI/CD pipeline using `localstack/setup-localstack`
- Nx workspace configuration (`nx.json`, `project.json` per service, `tsconfig.base.json`)

**Database Migration:**
- Schema extraction from PostgreSQL monolith tables (`entities`, `entity_relations`, `rec_*`, `app*`, `jobs`, `files`, `system_log`, `data_source`, `plugin_data`, `system_settings`)
- DynamoDB single-table design definitions per service
- RDS PostgreSQL schema definitions for Invoicing/Reporting
- FluentMigrator migration scripts for .NET services targeting RDS PostgreSQL

**Testing:**
- Unit tests per service (>80% coverage)
- Integration tests per service against LocalStack
- E2E tests for critical workflows against full LocalStack stack
- Contract tests for inter-service API and event schemas
- Frontend Playwright E2E tests + Vitest component/unit tests

**Documentation:**
- `README.md` — Updated for Nx monorepo structure and build instructions
- `docs/**/*.md` — Updated references for new architecture
- OpenAPI 3.1 specs per service
- JSON Schema event definitions in shared schema registry

### 0.3.2 Explicitly Out of Scope

- **Third-party SaaS integrations** (Stripe, external email providers) — stubbed interfaces only
- **Production AWS deployment** and AWS account configuration — CDK supports it but is not exercised
- **CloudFront CDN** — Skipped in LocalStack mode; exists only in production CDK stack conditional
- **X-Ray distributed tracing** — Replaced with correlation-ID structured logging locally; production CDK stack only
- **Performance load testing infrastructure** — No load testing tooling or scripts
- **Mobile application** — Not in scope; API designed to be mobile-consumable but no native app
- **Data warehouse / BI tooling** — Beyond the basic Reporting & Analytics service
- **Bulgarian FTS (Full-Text Search) analysis** — The `Fts/BulStem/` embedded resources are language-specific and may be deferred to a future localization pass
- **Blazor WebAssembly project** (`WebVella.Erp.WebAssembly/`) — Entirely replaced by React SPA; not ported
- **Console application** (`WebVella.Erp.ConsoleApp/`) — Development harness; not replicated
- **NuGet packaging** (`create-nuget-pkgs.bat`, `.nuspec` files) — Replaced by Nx library publishing
- **IIS hosting configuration** (`web.config`, publish profiles) — Replaced by Lambda + S3 hosting
- **Variant site hosts** (`WebVella.Erp.Site.Sdk/`, `.Next/`, `.Project/`, `.Mail/`, `.Crm/`, `.MicrosoftCDM/`) — Plugin composition replaced by independent microservices


## 0.4 Target Design


### 0.4.1 Refactored Structure Planning

The target is an Nx monorepo containing all services, the React frontend, shared libraries, and CDK infrastructure. Every file required for standalone operation is listed below.

```
webvella-erp/                              (Nx monorepo root)
├── nx.json                                (Nx workspace configuration)
├── package.json                           (Root package.json with Nx, CDK, shared devDeps)
├── tsconfig.base.json                     (Base TypeScript config)
├── .gitignore
├── .blitzyignore
├── .prettierrc
├── .eslintrc.json
├── docker-compose.yml                     (LocalStack Pro + Step Functions Local sidecar)
├── README.md
│
├── apps/
│   ├── frontend/                          (React SPA - Vite 6)
│   │   ├── package.json
│   │   ├── vite.config.ts
│   │   ├── tailwind.config.ts
│   │   ├── tsconfig.json
│   │   ├── tsconfig.app.json
│   │   ├── index.html
│   │   ├── public/
│   │   │   └── favicon.ico
│   │   ├── src/
│   │   │   ├── main.tsx
│   │   │   ├── App.tsx
│   │   │   ├── router.tsx                 (React Router 7 route definitions)
│   │   │   ├── api/
│   │   │   │   ├── client.ts              (Axios/fetch wrapper with AWS_ENDPOINT_URL override)
│   │   │   │   ├── auth.ts                (Cognito JWT token management)
│   │   │   │   └── endpoints/             (Per-domain API modules)
│   │   │   ├── stores/                    (Zustand stores for client state)
│   │   │   ├── hooks/                     (TanStack Query hooks per domain)
│   │   │   ├── pages/                     (Route-level page components)
│   │   │   │   ├── auth/                  (Login, Logout)
│   │   │   │   ├── home/                  (Dashboard)
│   │   │   │   ├── entities/              (Entity management)
│   │   │   │   ├── records/               (Record CRUD)
│   │   │   │   ├── crm/                   (CRM contacts/accounts)
│   │   │   │   ├── projects/              (Tasks, timelogs, comments)
│   │   │   │   ├── invoicing/             (Quotes, invoices, payments)
│   │   │   │   ├── inventory/             (Products, stock)
│   │   │   │   ├── reports/               (Dashboards, analytics)
│   │   │   │   ├── notifications/         (Email, in-app)
│   │   │   │   ├── files/                 (Document management)
│   │   │   │   ├── workflows/             (Approval chains)
│   │   │   │   ├── plugins/               (Extension management)
│   │   │   │   └── admin/                 (SDK admin: entities, fields, roles)
│   │   │   ├── components/                (Shared UI components)
│   │   │   │   ├── layout/               (AppShell, Sidebar, Nav, Header)
│   │   │   │   ├── fields/               (25+ field type components)
│   │   │   │   ├── data-table/           (Sortable, filterable data grid)
│   │   │   │   ├── forms/                (Dynamic form builder)
│   │   │   │   └── common/               (Button, Modal, Drawer, Tabs, Chart)
│   │   │   ├── types/                     (TypeScript interfaces from DTOs)
│   │   │   └── utils/                     (Formatters, validators, constants)
│   │   └── tests/
│   │       ├── e2e/                       (Playwright E2E tests)
│   │       └── unit/                      (Vitest component tests)
│   │
│   └── frontend-e2e/                      (Playwright E2E project)
│       ├── playwright.config.ts
│       └── src/
│
├── services/
│   ├── identity/                          (Identity & Access Management)
│   │   ├── src/
│   │   │   ├── Functions/                 (.NET 9 Lambda handlers)
│   │   │   ├── Models/                    (User, Role DTOs)
│   │   │   ├── Services/                  (Cognito integration, user migration)
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Identity.csproj
│   │
│   ├── entity-management/                 (Core Entity Engine)
│   │   ├── src/
│   │   │   ├── Functions/                 (Entity/field/relation/record Lambda handlers)
│   │   │   ├── Models/                    (Entity, Field, Relation, Record types)
│   │   │   ├── Services/                  (Metadata management, EQL adapter)
│   │   │   ├── DataAccess/                (DynamoDB single-table design)
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── EntityManagement.csproj
│   │
│   ├── crm/                               (CRM / Contacts)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/                    (Account, Contact, Address)
│   │   │   ├── Services/
│   │   │   ├── DataAccess/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Crm.csproj
│   │
│   ├── inventory/                         (Inventory / Products)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/
│   │   │   ├── DataAccess/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Inventory.csproj
│   │
│   ├── invoicing/                         (Invoicing / Billing — RDS PostgreSQL)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/
│   │   │   ├── DataAccess/                (RDS PostgreSQL via Npgsql)
│   │   │   ├── Migrations/               (FluentMigrator scripts)
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Invoicing.csproj
│   │
│   ├── reporting/                         (Reporting & Analytics — RDS PostgreSQL)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/
│   │   │   ├── DataAccess/
│   │   │   ├── Migrations/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Reporting.csproj
│   │
│   ├── notifications/                     (Notifications)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/                  (Email via SES stub, in-app, webhooks)
│   │   │   ├── DataAccess/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Notifications.csproj
│   │
│   ├── file-management/                   (File Management)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/                  (S3 upload/download/management)
│   │   │   ├── DataAccess/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── FileManagement.csproj
│   │
│   ├── workflow/                          (Workflow Engine — Step Functions)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/
│   │   │   ├── StateMachines/            (Step Function definitions)
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── Workflow.csproj
│   │
│   ├── plugin-system/                     (Plugin / Extension System)
│   │   ├── src/
│   │   │   ├── Functions/
│   │   │   ├── Models/
│   │   │   ├── Services/
│   │   │   ├── DataAccess/
│   │   │   └── project.json
│   │   ├── tests/
│   │   └── PluginSystem.csproj
│   │
│   └── authorizer/                        (Custom Lambda JWT Authorizer)
│       ├── src/
│       │   ├── index.ts                   (Node.js 22 Lambda handler)
│       │   ├── jwt-validator.ts
│       │   └── project.json
│       ├── tests/
│       └── package.json
│
├── libs/
│   ├── shared-schemas/                    (Event/API contract definitions)
│   │   ├── src/
│   │   │   ├── events/                    (JSON Schema event definitions)
│   │   │   ├── api/                       (OpenAPI 3.1 specs per service)
│   │   │   └── index.ts
│   │   └── project.json
│   │
│   ├── shared-cdk-constructs/             (Reusable CDK patterns)
│   │   ├── src/
│   │   │   ├── lambda-service.ts          (Standard Lambda + API GW construct)
│   │   │   ├── dynamodb-table.ts          (Standard DynamoDB construct)
│   │   │   ├── event-bus.ts               (SNS/SQS construct)
│   │   │   └── index.ts
│   │   └── project.json
│   │
│   ├── shared-ui/                         (React component library)
│   │   ├── src/
│   │   │   ├── components/                (DataTable, Form, FieldComponents)
│   │   │   ├── hooks/                     (useAuth, useApi, usePagination)
│   │   │   ├── types/                     (Shared TypeScript interfaces)
│   │   │   └── index.ts
│   │   └── project.json
│   │
│   └── shared-utils/                      (Cross-service utilities)
│       ├── src/
│       │   ├── correlation-id.ts
│       │   ├── logger.ts
│       │   ├── idempotency.ts
│       │   └── index.ts
│       └── project.json
│
├── infra/                                 (CDK Infrastructure)
│   ├── src/
│   │   ├── app.ts                         (CDK app entry point)
│   │   ├── stacks/
│   │   │   ├── identity-stack.ts
│   │   │   ├── entity-management-stack.ts
│   │   │   ├── crm-stack.ts
│   │   │   ├── inventory-stack.ts
│   │   │   ├── invoicing-stack.ts
│   │   │   ├── reporting-stack.ts
│   │   │   ├── notifications-stack.ts
│   │   │   ├── file-management-stack.ts
│   │   │   ├── workflow-stack.ts
│   │   │   ├── plugin-system-stack.ts
│   │   │   ├── api-gateway-stack.ts
│   │   │   ├── frontend-stack.ts
│   │   │   └── shared-stack.ts            (Cognito, SNS topics, shared resources)
│   │   └── constructs/                    (Reusable CDK patterns)
│   ├── cdk.json
│   ├── tsconfig.json
│   └── project.json
│
├── tools/
│   ├── scripts/
│   │   ├── bootstrap-localstack.sh        (cdklocal bootstrap + deploy)
│   │   ├── seed-test-data.sh              (Cognito users + test data)
│   │   └── run-migrations.sh              (FluentMigrator execution)
│   └── generators/                        (Nx custom generators)
│
└── .github/
    └── workflows/
        ├── ci.yml                         (PR checks with LocalStack)
        ├── deploy.yml                     (Production deploy)
        └── e2e.yml                        (E2E test suite)
```

### 0.4.2 Design Pattern Applications

- **Database-Per-Service** — Each bounded context owns its datastore. DynamoDB with single-table design for most services; RDS PostgreSQL with schema-level isolation for Invoicing/Billing and Reporting
- **API Gateway Pattern** — HTTP API Gateway (v2) as single entry point with path-based routing to per-domain Lambda functions. API versioning via `/v1/` path prefix
- **Event-Driven Architecture** — SNS topics for domain events (`{domain}.{entity}.{action}`), SQS queues for consumer decoupling, DLQs for all consumers
- **CQRS (partial)** — Reporting service consumes events from all domains to build read-optimized projections in RDS PostgreSQL
- **Saga Pattern** — Step Functions Local orchestrates cross-domain workflows (e.g., invoice creation → inventory update → notification)
- **Strangler Fig** — The monolith's `WebApiController` endpoints map 1:1 to Lambda handlers, enabling incremental validation

### 0.4.3 User Interface Design

The React SPA replaces all server-rendered Razor Pages and ViewComponents:

- **Application Shell** — `_AppMaster.cshtml` layout (nav + sidebar + content area) → React `AppShell` component with responsive Tailwind layout
- **Navigation** — `Nav`, `SiteMenu`, `ApplicationMenu`, `SidebarMenu` ViewComponents → React `Sidebar`, `TopNav`, `Breadcrumb` components with React Router integration
- **Page Builder Components** — 50+ `Pc*` ViewComponents → React domain-specific page components with lazy loading per route
- **Field Components** — 25+ `PcField*` ViewComponents (`PcFieldText`, `PcFieldDate`, `PcFieldSelect`, etc.) → React `FieldRenderer` with dynamic type dispatch and Tailwind styling
- **Data Grid** — `PcGrid` ViewComponent → React `DataTable` component with TanStack Table integration
- **Forms** — `PcForm` ViewComponent → React dynamic form builder with field validation
- **Authentication** — `login.cshtml`/`logout.cshtml` → React auth pages with Cognito integration
- **CRUD Workflows** — `RecordCreate/Details/List/Manage` pages → React route-based CRUD views with TanStack Query mutations
- **Admin Console** — SDK plugin pages → React admin module with entity/field/role editors


## 0.5 Transformation Mapping


### 0.5.1 File-by-File Transformation Plan

The entire refactoring executes in **one phase**. Every target file is mapped to its source below.

**Monorepo Root Configuration:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `nx.json` | CREATE | `WebVella.ERP3.sln` | Nx workspace config replacing VS solution; define task pipelines, caching, affected commands |
| `package.json` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | Root dependencies: Nx, CDK, TypeScript, shared devDeps |
| `tsconfig.base.json` | CREATE | `WebVella.Erp.Web/WebVella.Erp.Web.csproj` | Base TS config with path aliases for libs/* |
| `docker-compose.yml` | CREATE | `WebVella.Erp.Site/Config.json` | LocalStack Pro + Step Functions Local sidecar definition |
| `.gitignore` | CREATE | `.gitattributes` | Standard monorepo ignores + LocalStack artifacts |
| `.blitzyignore` | CREATE | — | `node_modules/`, `.localstack/`, `volume/`, `localstack/`, `cdk.out/`, `*.env`, `.env.*`, `dist/`, `build/`, `coverage/`, `*.tfstate` |
| `README.md` | CREATE | `README.md` | Rewritten for Nx monorepo, LocalStack setup, build instructions |

**Identity & Access Management Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/identity/Identity.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda project with Native AOT, Amazon.Lambda.AspNetCoreServer |
| `services/identity/src/Functions/AuthHandler.cs` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Lambda handler for login/logout/token-refresh via Cognito |
| `services/identity/src/Functions/UserHandler.cs` | CREATE | `WebVella.Erp/Api/SecurityManager.cs` | Lambda handler for user CRUD (backed by Cognito + DynamoDB) |
| `services/identity/src/Functions/RoleHandler.cs` | CREATE | `WebVella.Erp/Api/SecurityManager.cs` | Lambda handler for role management |
| `services/identity/src/Models/User.cs` | CREATE | `WebVella.Erp/Api/Definitions.cs` | User model with Cognito attributes mapping |
| `services/identity/src/Models/Role.cs` | CREATE | `WebVella.Erp/Api/Definitions.cs` | Role model extracted from SystemIds |
| `services/identity/src/Services/CognitoService.cs` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Cognito user pool operations, user migration from MD5 |
| `services/identity/src/Services/PermissionService.cs` | CREATE | `WebVella.Erp/Api/SecurityContext.cs` | Permission checking logic decoupled from AsyncLocal |
| `services/identity/src/DataAccess/UserRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | DynamoDB user/role persistence |
| `services/identity/tests/` | CREATE | `WebVella.Erp/Api/SecurityManager.cs` | Unit + integration tests against LocalStack Cognito |

**Entity Management Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/entity-management/EntityManagement.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda project with DynamoDB SDK |
| `services/entity-management/src/Functions/EntityHandler.cs` | CREATE | `WebVella.Erp/Api/EntityManager.cs` | Lambda handler for entity CRUD operations |
| `services/entity-management/src/Functions/FieldHandler.cs` | CREATE | `WebVella.Erp/Api/EntityManager.cs` | Lambda handler for field CRUD operations |
| `services/entity-management/src/Functions/RelationHandler.cs` | CREATE | `WebVella.Erp/Api/EntityRelationManager.cs` | Lambda handler for relation CRUD operations |
| `services/entity-management/src/Functions/RecordHandler.cs` | CREATE | `WebVella.Erp/Api/RecordManager.cs` | Lambda handler for record CRUD with domain event publishing |
| `services/entity-management/src/Functions/DataSourceHandler.cs` | CREATE | `WebVella.Erp/Api/DataSourceManager.cs` | Lambda handler for datasource execution |
| `services/entity-management/src/Functions/SearchHandler.cs` | CREATE | `WebVella.Erp/Api/SearchManager.cs` | Lambda handler for search operations |
| `services/entity-management/src/Functions/ImportExportHandler.cs` | CREATE | `WebVella.Erp/Api/ImportExportManager.cs` | Lambda handler for CSV import/export |
| `services/entity-management/src/Models/*.cs` | CREATE | `WebVella.Erp/Api/Models/*` | Entity, Field (20+ types), Relation, Record DTOs |
| `services/entity-management/src/Services/EntityService.cs` | CREATE | `WebVella.Erp/Api/EntityManager.cs` | Entity metadata management with DynamoDB backing |
| `services/entity-management/src/Services/RecordService.cs` | CREATE | `WebVella.Erp/Api/RecordManager.cs` | Record operations with event publishing |
| `services/entity-management/src/Services/QueryAdapter.cs` | CREATE | `WebVella.Erp/Eql/EqlBuilder.cs` | EQL-like query adapter for DynamoDB queries |
| `services/entity-management/src/DataAccess/EntityRepository.cs` | CREATE | `WebVella.Erp/Database/DbEntityRepository.cs` | DynamoDB single-table design for entity metadata |
| `services/entity-management/src/DataAccess/RecordRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | DynamoDB record persistence |
| `services/entity-management/tests/` | CREATE | `WebVella.Erp/Api/EntityManager.cs` | Unit + integration tests |

**CRM / Contacts Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/crm/Crm.csproj` | CREATE | `WebVella.Erp.Plugins.Crm/WebVella.Erp.Plugins.Crm.csproj` | .NET 9 Lambda project |
| `services/crm/src/Functions/AccountHandler.cs` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | Lambda handler for account CRUD |
| `services/crm/src/Functions/ContactHandler.cs` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | Lambda handler for contact CRUD |
| `services/crm/src/Models/Account.cs` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | Account model from entity patch definitions |
| `services/crm/src/Models/Contact.cs` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs` | Contact model with salutation |
| `services/crm/src/Services/SearchService.cs` | CREATE | `WebVella.Erp.Plugins.Next/Services/SearchService.cs` | x_search field indexing for DynamoDB |
| `services/crm/src/DataAccess/CrmRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | DynamoDB single-table for CRM entities |
| `services/crm/tests/` | CREATE | `WebVella.Erp.Plugins.Next/Hooks/Api/*` | CRM integration tests |

**Invoicing / Billing Service (RDS PostgreSQL):**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/invoicing/Invoicing.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda with Npgsql + FluentMigrator |
| `services/invoicing/src/Functions/InvoiceHandler.cs` | CREATE | `WebVella.Erp/Api/RecordManager.cs` | Lambda handler for invoice CRUD with ACID transactions |
| `services/invoicing/src/Functions/PaymentHandler.cs` | CREATE | `WebVella.Erp/Api/RecordManager.cs` | Lambda handler for payment processing |
| `services/invoicing/src/DataAccess/InvoiceRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | RDS PostgreSQL with Npgsql (schema-isolated) |
| `services/invoicing/src/Migrations/*.cs` | CREATE | `WebVella.Erp/Database/DbRepository.cs` | FluentMigrator scripts for invoicing schema |
| `services/invoicing/tests/` | CREATE | — | Invoice workflow integration tests |

**Project Management / Inventory Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/inventory/Inventory.csproj` | CREATE | `WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj` | .NET 9 Lambda project |
| `services/inventory/src/Functions/TaskHandler.cs` | CREATE | `WebVella.Erp.Plugins.Project/Services/*` | Lambda handler for task CRUD |
| `services/inventory/src/Functions/TimelogHandler.cs` | CREATE | `WebVella.Erp.Plugins.Project/Controllers/*` | Lambda handler for timelog operations |
| `services/inventory/src/Services/TaskService.cs` | CREATE | `WebVella.Erp.Plugins.Project/Services/*` | Task business logic extraction |
| `services/inventory/src/DataAccess/InventoryRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | DynamoDB single-table design |
| `services/inventory/tests/` | CREATE | — | Inventory integration tests |

**Reporting & Analytics Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/reporting/Reporting.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda with Npgsql |
| `services/reporting/src/Functions/ReportHandler.cs` | CREATE | `WebVella.Erp/Api/DataSourceManager.cs` | Lambda handler for report generation |
| `services/reporting/src/Functions/EventConsumer.cs` | CREATE | `WebVella.Erp/Hooks/RecordHookManager.cs` | SQS consumer for event-sourced read model updates |
| `services/reporting/src/DataAccess/ReportRepository.cs` | CREATE | `WebVella.Erp/Database/DbDataSourceRepository.cs` | RDS PostgreSQL read-model projections |
| `services/reporting/src/Migrations/*.cs` | CREATE | — | FluentMigrator scripts for reporting schema |
| `services/reporting/tests/` | CREATE | — | Reporting integration tests |

**Notifications Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/notifications/Notifications.csproj` | CREATE | `WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj` | .NET 9 Lambda project |
| `services/notifications/src/Functions/EmailHandler.cs` | CREATE | `WebVella.Erp.Plugins.Mail/Services/*` | Lambda for email send/queue operations |
| `services/notifications/src/Functions/WebhookHandler.cs` | CREATE | `WebVella.Erp/Notifications/*` | Lambda for webhook dispatch |
| `services/notifications/src/Functions/QueueProcessor.cs` | CREATE | `WebVella.Erp.Plugins.Mail/Jobs/*` | SQS-triggered email queue processor |
| `services/notifications/src/Services/SmtpService.cs` | CREATE | `WebVella.Erp.Plugins.Mail/Services/*` | SMTP engine (stubbed for third-party) |
| `services/notifications/src/DataAccess/NotificationRepository.cs` | CREATE | `WebVella.Erp/Database/DbRecordRepository.cs` | DynamoDB for notification metadata |
| `services/notifications/tests/` | CREATE | — | Notification integration tests |

**File Management Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/file-management/FileManagement.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda with S3 SDK |
| `services/file-management/src/Functions/UploadHandler.cs` | CREATE | `WebVella.Erp/Database/DbFileRepository.cs` | Lambda for S3 presigned URL generation + upload |
| `services/file-management/src/Functions/DownloadHandler.cs` | CREATE | `WebVella.Erp/Database/DbFile.cs` | Lambda for file retrieval from S3 |
| `services/file-management/src/Services/S3Service.cs` | CREATE | `WebVella.Erp/Database/DbFileRepository.cs` | S3 operations replacing LO/filesystem/Storage.Net |
| `services/file-management/src/DataAccess/FileMetadataRepository.cs` | CREATE | `WebVella.Erp/Database/DbFileRepository.cs` | DynamoDB for file metadata |
| `services/file-management/tests/` | CREATE | — | File management integration tests |

**Workflow Engine Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/workflow/Workflow.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | .NET 9 Lambda with Step Functions SDK |
| `services/workflow/src/Functions/WorkflowHandler.cs` | CREATE | `WebVella.Erp/Jobs/JobManager.cs` | Lambda handler for workflow initiation |
| `services/workflow/src/Functions/StepHandler.cs` | CREATE | `WebVella.Erp/Jobs/JobPool.cs` | Lambda handler for individual Step Function steps |
| `services/workflow/src/StateMachines/*.json` | CREATE | `WebVella.Erp/Jobs/SheduleManager.cs` | Step Functions ASL definitions |
| `services/workflow/src/Services/WorkflowService.cs` | CREATE | `WebVella.Erp/Jobs/JobManager.cs` | Workflow orchestration via Step Functions |
| `services/workflow/tests/` | CREATE | — | Workflow integration tests |

**Plugin / Extension System Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/plugin-system/PluginSystem.csproj` | CREATE | `WebVella.Erp/ErpPlugin.cs` | .NET 9 Lambda project |
| `services/plugin-system/src/Functions/PluginHandler.cs` | CREATE | `WebVella.Erp/ErpPlugin.cs` | Lambda handler for plugin registration/listing |
| `services/plugin-system/src/Models/Plugin.cs` | CREATE | `WebVella.Erp/ErpPlugin.cs` | Plugin metadata model |
| `services/plugin-system/src/DataAccess/PluginRepository.cs` | CREATE | `WebVella.Erp/ErpPlugin.cs` | DynamoDB for plugin registry |
| `services/plugin-system/tests/` | CREATE | — | Plugin system integration tests |

**Custom Lambda Authorizer:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `services/authorizer/src/index.ts` | CREATE | `WebVella.Erp.Web/Middleware/JwtMiddleware.cs` | Node.js 22 JWT validation Lambda |
| `services/authorizer/src/jwt-validator.ts` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Generic JWT validation (Cognito + LocalStack) |
| `services/authorizer/package.json` | CREATE | — | jsonwebtoken, jwks-rsa dependencies |
| `services/authorizer/tests/` | CREATE | — | JWT validation unit tests |

**React SPA Frontend:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `apps/frontend/src/main.tsx` | CREATE | `WebVella.Erp.Site/Program.cs` | React app entry point |
| `apps/frontend/src/App.tsx` | CREATE | `WebVella.Erp.Web/Pages/_AppMaster.cshtml` | Root app component with router + auth provider |
| `apps/frontend/src/router.tsx` | CREATE | `WebVella.Erp.Web/Pages/*.cshtml.cs` | React Router 7 route config from Razor Page routes |
| `apps/frontend/src/api/client.ts` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | API client with AWS_ENDPOINT_URL override |
| `apps/frontend/src/api/auth.ts` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Cognito auth token management |
| `apps/frontend/src/pages/auth/Login.tsx` | CREATE | `WebVella.Erp.Web/Pages/login.cshtml` | React login page |
| `apps/frontend/src/pages/home/Dashboard.tsx` | CREATE | `WebVella.Erp.Web/Pages/Index.cshtml` | React dashboard page |
| `apps/frontend/src/pages/records/RecordList.tsx` | CREATE | `WebVella.Erp.Web/Pages/RecordList.cshtml` | React record list with data table |
| `apps/frontend/src/pages/records/RecordCreate.tsx` | CREATE | `WebVella.Erp.Web/Pages/RecordCreate.cshtml` | React record creation form |
| `apps/frontend/src/pages/records/RecordDetails.tsx` | CREATE | `WebVella.Erp.Web/Pages/RecordDetails.cshtml` | React record details view |
| `apps/frontend/src/pages/records/RecordManage.tsx` | CREATE | `WebVella.Erp.Web/Pages/RecordManage.cshtml` | React record edit form |
| `apps/frontend/src/pages/admin/*.tsx` | CREATE | `WebVella.Erp.Plugins.SDK/Pages/**/*` | React admin console pages |
| `apps/frontend/src/pages/projects/*.tsx` | CREATE | `WebVella.Erp.Plugins.Project/Components/*` | React project management pages |
| `apps/frontend/src/pages/crm/*.tsx` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | React CRM pages (accounts, contacts) |
| `apps/frontend/src/pages/notifications/*.tsx` | CREATE | `WebVella.Erp.Plugins.Mail/MailPlugin.20190215.cs` | React email/notification pages |
| `apps/frontend/src/components/layout/AppShell.tsx` | CREATE | `WebVella.Erp.Web/Pages/_AppMaster.cshtml` | App chrome: sidebar + nav + content |
| `apps/frontend/src/components/layout/Sidebar.tsx` | CREATE | `WebVella.Erp.Web/Components/SidebarMenu/*` | Sidebar navigation |
| `apps/frontend/src/components/layout/TopNav.tsx` | CREATE | `WebVella.Erp.Web/Components/Nav/*` | Top navigation bar |
| `apps/frontend/src/components/fields/*.tsx` | CREATE | `WebVella.Erp.Web/Components/PcField*/*` | 25+ field type React components |
| `apps/frontend/src/components/data-table/DataTable.tsx` | CREATE | `WebVella.Erp.Web/Components/PcGrid/*` | Data grid with sorting/filtering/pagination |
| `apps/frontend/src/components/forms/DynamicForm.tsx` | CREATE | `WebVella.Erp.Web/Components/PcForm/*` | Dynamic form builder |
| `apps/frontend/src/components/common/Modal.tsx` | CREATE | `WebVella.Erp.Web/Components/PcModal/*` | Modal dialog |
| `apps/frontend/src/components/common/Drawer.tsx` | CREATE | `WebVella.Erp.Web/Components/PcDrawer/*` | Side drawer |
| `apps/frontend/src/components/common/TabNav.tsx` | CREATE | `WebVella.Erp.Web/Components/PcTabNav/*` | Tab navigation |
| `apps/frontend/src/components/common/Chart.tsx` | CREATE | `WebVella.Erp.Web/Components/PcChart/*` | Chart component |
| `apps/frontend/src/types/*.ts` | CREATE | `WebVella.Erp.Web/Models/*.cs` | TypeScript interfaces from C# DTOs |
| `apps/frontend/src/stores/*.ts` | CREATE | `WebVella.Erp.Web/Models/BaseErpPageModel.cs` | Zustand stores replacing server-side page model |
| `apps/frontend/src/hooks/*.ts` | CREATE | `WebVella.Erp.Web/Services/*.cs` | TanStack Query hooks per domain |
| `apps/frontend/vite.config.ts` | CREATE | — | Vite config with env variable handling |
| `apps/frontend/tailwind.config.ts` | CREATE | `WebVella.Erp.Web/Theme/styles.css` | Tailwind config replacing Bootstrap 4 theme |
| `apps/frontend/package.json` | CREATE | — | React 19, Vite 6, Router 7, TanStack Query 5, Zustand 5, Tailwind 4 |

**CDK Infrastructure:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `infra/src/app.ts` | CREATE | `WebVella.Erp.Site/Startup.cs` | CDK app entry with localstack context flag |
| `infra/src/stacks/api-gateway-stack.ts` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | HTTP API v2 with route-to-Lambda mapping |
| `infra/src/stacks/identity-stack.ts` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Cognito + Identity Lambda + DynamoDB |
| `infra/src/stacks/entity-management-stack.ts` | CREATE | `WebVella.Erp/Api/EntityManager.cs` | Entity service Lambda + DynamoDB |
| `infra/src/stacks/shared-stack.ts` | CREATE | `WebVella.Erp.Site/Config.json` | Cognito user pool, SNS topics, SSM parameters |
| `infra/src/stacks/frontend-stack.ts` | CREATE | — | S3 bucket for SPA hosting |
| `infra/cdk.json` | CREATE | — | CDK config with localstack context |
| `infra/src/stacks/*.ts` (remaining) | CREATE | — | One stack per bounded context service |

**Shared Libraries:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `libs/shared-schemas/src/events/*.json` | CREATE | `WebVella.Erp/Hooks/IErp*Hook.cs` | JSON Schema event definitions from hook contracts |
| `libs/shared-schemas/src/api/*.yaml` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | OpenAPI 3.1 specs extracted from controller endpoints |
| `libs/shared-cdk-constructs/src/*.ts` | CREATE | — | Reusable CDK patterns for Lambda + API GW + DynamoDB |
| `libs/shared-ui/src/components/*.tsx` | CREATE | `WebVella.Erp.Web/Components/PcFieldBase/*` | Shared React field/form/table components |
| `libs/shared-utils/src/*.ts` | CREATE | `WebVella.Erp/Utilities/*` | Correlation-ID, logger, idempotency utilities |

**CI/CD:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `.github/workflows/ci.yml` | CREATE | `.github/FUNDING.yml` | GitHub Actions with localstack/setup-localstack |
| `.github/workflows/deploy.yml` | CREATE | — | Production CDK deploy pipeline |
| `.github/workflows/e2e.yml` | CREATE | — | E2E test pipeline |

### 0.5.2 Cross-File Dependencies

**Import Transformation Rules:**

- Old: `using WebVella.Erp.Api;` → New: Per-service namespace (e.g., `using WebVellaErp.Identity.Services;`)
- Old: `using WebVella.Erp.Database;` → New: Per-service data access (e.g., `using WebVellaErp.EntityManagement.DataAccess;`)
- Old: `DbContext.Current` (ambient singleton) → New: Injected `IDynamoDbContext` or `INpgsqlContext` per service
- Old: `SecurityContext.OpenScope(user)` (AsyncLocal) → New: JWT claims extracted from Lambda event context
- Old: `HookManager.GetHookedInstances<T>()` → New: SNS topic publish for domain events
- Old: `RecordManager.CreateRecord()` → New: Domain-specific service method + SNS event publish
- Old: `import $ from 'jquery'` (frontend) → New: React hooks and state management
- Old: Razor `@Html.Raw()` / `@ViewBag` → New: React JSX with props
- Old: `<wv-field-text>` tag helpers → New: `<TextField />` React components
- Old: `bootstrap.css` → New: `tailwind.config.ts` utility classes

### 0.5.3 Wildcard Patterns

- `services/*/src/Functions/*.cs` — All Lambda handler files across all services
- `services/*/src/Models/*.cs` — All domain model files
- `services/*/src/Services/*.cs` — All service layer files
- `services/*/src/DataAccess/*.cs` — All data access layer files
- `services/*/tests/**/*.cs` — All test files
- `apps/frontend/src/pages/**/*.tsx` — All route-level page components
- `apps/frontend/src/components/**/*.tsx` — All shared UI components
- `apps/frontend/src/hooks/*.ts` — All TanStack Query hooks
- `apps/frontend/src/stores/*.ts` — All Zustand stores
- `apps/frontend/src/types/*.ts` — All TypeScript interface files
- `infra/src/stacks/*.ts` — All CDK stack definitions
- `libs/*/src/**/*.ts` — All shared library source files
- `.github/workflows/*.yml` — All CI/CD pipeline definitions


## 0.6 Dependency Inventory


### 0.6.1 Key Private and Public Packages

**Frontend Dependencies (React SPA):**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm | react | 19.x | UI framework |
| npm | react-dom | 19.x | React DOM renderer |
| npm | react-router | 7.x | Client-side routing |
| npm | @tanstack/react-query | 5.x | Server state management (API data fetching/caching) |
| npm | zustand | 5.x | Client-only UI state management |
| npm | tailwindcss | 4.x | Utility-first CSS framework (replaces Bootstrap 4) |
| npm | vite | 6.x | Build tooling, HMR, bundling |
| npm | @vitejs/plugin-react | latest | Vite React plugin |
| npm | typescript | 5.x | TypeScript compiler |
| npm | vitest | latest | Unit/component test runner |
| npm | @playwright/test | latest | E2E test framework |
| npm | axios | latest | HTTP client for API calls |
| npm | @aws-sdk/client-cognito-identity-provider | latest | Cognito auth SDK |

**Backend Dependencies (.NET 9 Lambda Services):**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| NuGet | Amazon.Lambda.AspNetCoreServer | latest | ASP.NET Core Lambda hosting |
| NuGet | Amazon.Lambda.Core | latest | Lambda runtime interface |
| NuGet | Amazon.Lambda.Serialization.SystemTextJson | latest | JSON serialization for Lambda |
| NuGet | AWSSDK.DynamoDBv2 | latest | DynamoDB SDK |
| NuGet | AWSSDK.S3 | latest | S3 SDK |
| NuGet | AWSSDK.SimpleNotificationService | latest | SNS SDK |
| NuGet | AWSSDK.SQS | latest | SQS SDK |
| NuGet | AWSSDK.StepFunctions | latest | Step Functions SDK |
| NuGet | AWSSDK.SSM | latest | SSM Parameter Store SDK |
| NuGet | AWSSDK.CognitoIdentityProvider | latest | Cognito SDK |
| NuGet | Npgsql | 9.0.4 | PostgreSQL ADO.NET (Invoicing/Reporting only) |
| NuGet | FluentMigrator | latest | Database migration (RDS PostgreSQL services) |
| NuGet | FluentMigrator.Runner | latest | Migration runner |
| NuGet | Newtonsoft.Json | 13.0.4 | JSON serialization (compatibility) |
| NuGet | AutoMapper | 14.0.0 | Object mapping (within services) |
| NuGet | CsvHelper | 33.1.0 | CSV import/export (Entity Management) |

**Node.js Lambda Authorizer Dependencies:**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm | jsonwebtoken | latest | JWT decode/verify |
| npm | jwks-rsa | latest | JWKS key resolution for Cognito |
| npm | @types/aws-lambda | latest | Lambda event type definitions |

**Infrastructure Dependencies:**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm | aws-cdk-lib | 2.x | AWS CDK core library |
| npm | constructs | 10.x | CDK constructs base |
| npm | aws-cdk | 2.x | CDK CLI |
| npm | aws-cdk-local | 3.x | LocalStack CDK wrapper |
| npm | nx | latest | Monorepo orchestration |
| npm | @nx/vite | latest | Nx Vite plugin |
| npm | @nx/react | latest | Nx React plugin |
| npm | @nx/js | latest | Nx JavaScript/TypeScript plugin |

**Testing Dependencies:**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| NuGet | xunit | latest | .NET unit test framework |
| NuGet | Moq | latest | .NET mocking library |
| NuGet | FluentAssertions | latest | .NET assertion library |
| npm | vitest | latest | Frontend unit/component tests |
| npm | @playwright/test | latest | Frontend E2E tests |
| npm | @testing-library/react | latest | React component testing utilities |

**Tooling Dependencies (global installs):**

| Registry | Package | Version | Purpose |
|----------|---------|---------|---------|
| npm (global) | aws-cdk-local | 3.x | `cdklocal` CLI wrapper |
| npm (global) | aws-cdk | 2.x | `cdk` CLI |
| pip (global) | awscli-local | latest | `awslocal` CLI wrapper |
| system | Node.js | 22 LTS | JavaScript runtime |
| system | .NET SDK | 9.0 | .NET build/runtime |
| system | Docker | latest | Container runtime for LocalStack |

### 0.6.2 Dependency Updates

**Import Refactoring Patterns:**

Files requiring import updates (by pattern):
- `services/**/*.cs` — All .NET service files need new namespace imports replacing `WebVella.Erp.*`
- `apps/frontend/src/**/*.ts` — All frontend files use new `@webvella-erp/*` package aliases
- `apps/frontend/src/**/*.tsx` — All React component files
- `libs/**/*.ts` — All shared library files

**Import Transformation Rules:**

- Old: `using WebVella.Erp.Api;` → New: `using WebVellaErp.{ServiceName}.Services;`
- Old: `using WebVella.Erp.Database;` → New: `using WebVellaErp.{ServiceName}.DataAccess;`
- Old: `using WebVella.Erp.Api.Models;` → New: `using WebVellaErp.{ServiceName}.Models;`
- Old: `using Newtonsoft.Json;` → New: `using System.Text.Json;` (where possible for AOT compat)
- Old: `import 'bootstrap'` → New: Tailwind CSS utility classes (no JS import)
- Old: `import $ from 'jquery'` → New: React hooks / DOM refs
- Old: `using WebVella.Erp.Hooks;` → New: SNS event publishing via `AWSSDK.SimpleNotificationService`

**External Reference Updates:**

- `infra/cdk.json` — CDK context for localstack flag
- `docker-compose.yml` — LocalStack Pro + Step Functions Local service definitions
- `.github/workflows/*.yml` — CI/CD pipeline references to LocalStack setup action
- `services/*/project.json` — Nx project configuration per service
- `apps/frontend/.env.local` — `VITE_API_URL` for LocalStack HTTP API Gateway


## 0.7 Special Analysis


### 0.7.1 EQL Engine Decomposition Strategy

The WebVella ERP monolith's Entity Query Language (EQL) engine (`WebVella.Erp/Eql/` — 13 source files) is a critical cross-cutting system that must be decomposed across the target microservices architecture. The EQL engine currently:

- Parses SELECT queries with an Irony-based grammar (`EqlGrammar.cs`) supporting WHERE, ORDER BY, PAGE, PAGESIZE clauses
- Translates AST to PostgreSQL SQL with `row_to_json()` for JSON-shaped results (`EqlBuilder.Sql.cs`)
- Supports relation navigation via `$relation` / `$$relation` notation with correlated subqueries
- Executes against a single PostgreSQL instance with 600-second timeout (`EqlCommand.cs`)
- Runs pre/post search hooks (`RecordHookManager.ExecutePreSearchRecordHooks`)

**Target Decomposition:**

In the serverless architecture, EQL functionality splits into:
- **Entity Management Service** — Owns the query adapter that translates EQL-like query syntax into DynamoDB `Query`/`Scan` operations for entities stored in DynamoDB
- **Invoicing / Reporting Services** — Use standard Npgsql SQL (no EQL translation needed) since they connect to RDS PostgreSQL directly
- **Frontend API Client** — Translates user-facing filter/sort/page parameters into REST query parameters rather than raw EQL strings
- **Cross-Service Queries** — Replaced by API composition: the frontend or an aggregating Lambda fetches from multiple service endpoints

### 0.7.2 Hook System to Event-Driven Architecture Migration

The monolith's hook system (`WebVella.Erp/Hooks/` — 21 files) provides synchronous pre/post interception for all CRUD operations. This must map to the target's async event-driven architecture:

**Current Hook Contracts → Target Events:**

| Hook Interface | Direction | Target Event Pattern |
|---------------|-----------|---------------------|
| `IErpPreCreateRecordHook` | Sync, blocking | API-level validation in Lambda handler (no event needed) |
| `IErpPostCreateRecordHook` | Sync, non-blocking | SNS event: `{domain}.{entity}.created` |
| `IErpPreUpdateRecordHook` | Sync, blocking | API-level validation in Lambda handler |
| `IErpPostUpdateRecordHook` | Sync, non-blocking | SNS event: `{domain}.{entity}.updated` |
| `IErpPreDeleteRecordHook` | Sync, blocking | API-level validation in Lambda handler |
| `IErpPostDeleteRecordHook` | Sync, non-blocking | SNS event: `{domain}.{entity}.deleted` |
| `IErpPreSearchRecordHook` | Sync, blocking | Query validation in service layer |
| `IErpPostSearchRecordHook` | Sync, non-blocking | Not needed (search results are synchronous) |
| `IErpPre/PostCreateManyToManyRelationHook` | Sync | SNS event: `{domain}.relation.{created/deleted}` |

**Key consideration:** Pre-hooks that modify data before persistence must remain synchronous within the owning service's Lambda handler. Post-hooks that notify other systems become SNS-published domain events consumed by SQS queues.

### 0.7.3 Dynamic Entity/Field System Architecture

The monolith's most complex subsystem is the dynamic entity/field definition engine. Currently, `EntityManager` stores entity metadata as JSON documents in the `entities` table and dynamically creates PostgreSQL `rec_{entityName}` tables with columns matching field definitions. This system supports 20+ field types (`DbBaseField` subclasses in `WebVella.Erp/Database/FieldTypes/`).

**Target Architecture for Dynamic Entities:**

- The **Entity Management** service owns all entity metadata in a DynamoDB table with `PK=ENTITY#{entityId}` and `SK=META` for entity definitions, `SK=FIELD#{fieldId}` for field definitions, `SK=RELATION#{relationId}` for relation definitions
- Record storage uses per-entity items in a separate DynamoDB table with `PK=ENTITY#{entityName}` and `SK=RECORD#{recordId}`
- Field type validation and transformation logic from `RecordManager.cs` (lines handling auto-numbers, passwords, dates, files, geography, multiselect arrays) must be replicated in the Entity Management service's record processing pipeline
- The 20+ field types from `WebVella.Erp/Database/FieldTypes/` are mapped to DynamoDB attribute types with type-specific serialization

### 0.7.4 Database Migration Strategy

**Source Schema (Single PostgreSQL):**

| Table | Owner in Target | Target Datastore |
|-------|----------------|-----------------|
| `entities` (JSON doc store) | Entity Management | DynamoDB |
| `entity_relations` (JSON doc store) | Entity Management | DynamoDB |
| `rec_{entityName}` (per-entity tables) | Owning domain service | DynamoDB / RDS PostgreSQL |
| `app`, `app_page`, `app_sitemap_*` | Plugin System | DynamoDB |
| `jobs`, `schedule_plans` | Workflow Engine | DynamoDB |
| `files` | File Management | DynamoDB (metadata) + S3 (content) |
| `system_log` | All services (structured logging) | CloudWatch Logs |
| `data_source` | Entity Management | DynamoDB |
| `plugin_data` | Plugin System | DynamoDB |
| `system_settings` | Shared (SSM Parameter Store) | SSM |
| `system_search` | Entity Management | DynamoDB (with GSI for search) |

### 0.7.5 Authentication Migration Path

The monolith uses MD5-hashed passwords (`SecurityManager.cs` uses `CryptoUtility.ComputeOddMD5Hash`) for credential validation. Cognito does not support MD5 directly.

**Migration Strategy:**
- Deploy a **User Migration Lambda Trigger** on the Cognito user pool
- On first login attempt, the trigger calls the legacy password verification logic
- If the MD5 hash matches, the Lambda creates the user in Cognito with the provided password (Cognito hashes it securely)
- Subsequent logins use Cognito natively
- The default system user (`erp@webvella.com` / `erp`) is seeded during Cognito user pool bootstrapping
- System roles (`SystemIds.AdministratorRoleId`, `SystemIds.RegularRoleId`, `SystemIds.GuestRoleId`) map to Cognito groups

### 0.7.6 LocalStack Dual-Target CDK Strategy

CDK stacks must deploy against both LocalStack and production AWS using a single codebase. The `localstack` context flag controls resource selection:

```
const isLocalStack = this.node.tryGetContext('localstack') === 'true';
```

**Conditional Resources:**

| Resource | LocalStack Mode | Production Mode |
|----------|----------------|-----------------|
| Database | RDS PostgreSQL (via `awslocal rds`) | Aurora Serverless v2 |
| CDN | Skipped | CloudFront distribution |
| Tracing | Correlation-ID structured logging | X-Ray |
| Frontend Hosting | S3 website hosting | S3 + CloudFront |
| Certificates | Skipped | ACM |
| DNS | Skipped | Route 53 |

### 0.7.7 Frontend Component Mapping Analysis

The 50+ Razor ViewComponents in `WebVella.Erp.Web/Components/` must be systematically mapped to React components:

**Layout Components:**
- `PcRow` (12-column grid) → React `Row` component with Tailwind CSS grid (`grid-cols-12`)
- `PcGrid` (data table with paging) → React `DataTable` with TanStack Table
- `PcSection` (collapsible cards) → React `Section` with Tailwind accordion patterns
- `PcTabNav` (tabs/pills) → React `Tabs` component
- `PcForm` (form wrapper) → React `DynamicForm` with field validation
- `PcModal` / `PcDrawer` → React `Modal` / `Drawer` with portal rendering

**Field Components (25+ types):**
- Each `PcField*` ViewComponent has display/edit modes via `ComponentMode` enum
- React equivalents use controlled components with `value`/`onChange` props
- Field type dispatch: a single `FieldRenderer` component that maps `fieldType` to the specific React field component

**Navigation Components:**
- `Nav` + `script.js` (dropdown behavior) → React `TopNav` with state-managed dropdowns
- `SiteMenu` / `ApplicationMenu` / `SidebarMenu` → React `Sidebar` with React Router `NavLink`
- `UserMenu` / `UserNav` → React `UserDropdown` with Cognito user info


## 0.8 Refactoring Rules


### 0.8.1 User-Specified Rules and Requirements

The following rules are explicitly mandated by the user and must be enforced across all generated code:

- **Full behavioral parity** — All existing business logic MUST be preserved functionally. No business logic may be omitted. Every entity, every field type, every CRUD operation, every workflow present in the monolith must have a functional equivalent in the target microservices
- **Self-contained bounded contexts** — Each bounded context service MUST be self-contained with its own datastore, own Lambda functions, own API routes, and own tests. Zero cross-service database access is permitted
- **Pure static SPA** — The frontend MUST be a pure static SPA. Zero server-side rendering. Zero Lambda@Edge. Zero API routes in the frontend application
- **LocalStack runtime dependency only** — The LocalStack repository, image layers, and source code MUST NEVER appear in the generated codebase. Only `docker-compose.yml` references to the Docker image are permitted. LocalStack is a Docker image pull, NEVER a repository clone
- **LocalStack-exclusive testing** — All integration and E2E tests MUST execute against LocalStack. No mocked AWS SDK calls in integration tests. The pattern is: `docker compose up -d` → test → `docker compose down`
- **Dual-target CDK** — CDK stacks MUST work against both LocalStack (`cdklocal --context localstack=true`) and production AWS (`cdk deploy`) with the same codebase. Dual-target via context flag, not separate stacks
- **Single entity ownership** — Every entity MUST have exactly one owning service. Tables owned by a service MUST NOT be directly queried by any other service
- **Data integrity during migration** — Zero data loss during migration. Existing data in the PostgreSQL monolith must be migrateable to the per-service datastores
- **Backward-compatible endpoints** — Existing API consumers (if any) MUST receive backward-compatible endpoints or a documented migration path

### 0.8.2 Performance Requirements

| Metric | Target |
|--------|--------|
| Lambda cold start (.NET Native AOT) | < 1 second |
| Lambda cold start (Node.js) | < 3 seconds |
| API response P95 (warm) | < 500ms |
| DynamoDB read latency P99 | < 10ms |
| SQS message processing latency | < 5 seconds end-to-end |
| Frontend Time-to-Interactive (4G) | < 2 seconds |
| Step Functions workflow completion | < 30 seconds (standard approval chains) |
| Vite production build | < 30 seconds |
| Per-route chunk size | < 200KB gzipped |
| Lambda package size (unzipped) | < 250MB per function |

### 0.8.3 Security Requirements

- Cognito JWT validation via HTTP API native JWT authorizer (custom Lambda authorizer fallback for LocalStack)
- IAM least-privilege per Lambda function
- Encryption at rest for all datastores (DynamoDB, RDS, S3)
- Encryption in transit (TLS 1.3) for all service communication
- OWASP Top 10 compliance across all services and frontend
- Input validation at API Gateway level (request schemas) + service level
- Frontend: no secrets in bundle, CORS locked to known origins
- All secrets via SSM Parameter Store SecureString — NEVER environment variables

### 0.8.4 Testing Requirements

- Unit test coverage > 80% per service
- Integration tests per service running against LocalStack
- End-to-end tests for all critical user workflows against full LocalStack stack
- Contract tests for all inter-service API and event schemas
- Frontend: Playwright E2E tests for all user flows, Vitest for component/unit tests
- All integration and E2E tests MUST run against LocalStack — no mocked AWS SDK calls

### 0.8.5 Operational Requirements

- Structured JSON logging with correlation-ID propagation from all Lambda functions
- Health check endpoints per service
- Dead-letter queues for all SQS consumers with naming convention `{service}-{queue}-dlq`
- Event naming convention: `{domain}.{entity}.{action}` (e.g., `invoicing.invoice.created`)
- At-least-once delivery guarantee via SQS
- All event consumers MUST be idempotent
- Idempotency keys on all write endpoints and event handlers

### 0.8.6 Special Instructions and Constraints

- **.blitzyignore enforcement** — The following patterns must be in `.blitzyignore` and `.gitignore`: `node_modules/`, `.localstack/`, `volume/`, `localstack/`, `cdk.out/`, `*.env`, `.env.*`, `dist/`, `build/`, `coverage/`, `*.tfstate`
- **Environment variables** — `AWS_ENDPOINT_URL` (http://localhost:4566 for LocalStack, omitted in production), `AWS_REGION` (us-east-1), `COGNITO_USER_POOL_ID`, `API_GATEWAY_URL`, `IS_LOCAL` (true when targeting LocalStack), `VITE_API_URL` (Vite env prefix for frontend)
- **Secrets via SSM** — `DB_CONNECTION_STRING` and `COGNITO_CLIENT_SECRET` stored as SSM SecureString, never environment variables
- **One-phase execution** — The entire refactoring executes in a single phase. ALL files are generated in one pass
- **No temporal planning** — No week-by-week schedules or phase timelines. Focus on structure and implementation mapping
- **API versioning** — Path-based versioning (`/v1/`) at HTTP API Gateway level
- **Event schemas** — Defined in JSON Schema, published to shared schema registry package (`libs/shared-schemas/`)
- **Communication patterns** — HTTP API (v2) for sync calls; SQS for async point-to-point; SNS fan-out only for multi-consumer events; no service mesh


## 0.9 References


### 0.9.1 Source Repository Files and Folders Searched

The following files and folders were comprehensively inspected to derive the conclusions in this Agent Action Plan:

**Root-level files:**
- `.gitattributes` — Git attributes configuration
- `LIBRARIES.md` — Third-party library inventory placeholder
- `LICENSE.txt` — Apache License 2.0
- `README.md` — Project landing page (ASP.NET Core 9 + PostgreSQL 16 stack description)
- `WebVella.ERP3.sln` — Visual Studio solution file (15+ projects)
- `create-nuget-pkgs.bat` — NuGet packaging automation
- `global.json` — .NET SDK version hook (version commented out)

**Core Engine (`WebVella.Erp/`):**
- `WebVella.Erp.csproj` — Project definition (net10.0, dependencies: Npgsql 9.0.4, AutoMapper 14.0.0, Newtonsoft.Json 13.0.4, Irony.NetCore 1.1.11, CsvHelper 33.1.0, Ical.Net 5.1.4, Storage.Net 9.3.0)
- `ERPService.cs` — Bootstrap orchestrator
- `ErpPlugin.cs` — Abstract plugin base
- `ErpSettings.cs` — Global configuration binder
- `IErpService.cs` — Service initialization contract
- `Api/` — Complete folder: Cache.cs, DataSourceManager.cs, Definitions.cs, EntityManager.cs, EntityRelationManager.cs, ImportExportManager.cs, RecordManager.cs, SearchManager.cs, SecurityContext.cs, SecurityManager.cs, Models/
- `Database/` — Complete folder: DbContext.cs, DbConnection.cs, DbRepository.cs, DbEntityRepository.cs, DbRelationRepository.cs, DbRecordRepository.cs, DbFileRepository.cs, DBTypeConverter.cs, DbDataSourceRepository.cs, DbSystemSettings.cs, AutoMapper/, FieldTypes/
- `Eql/` — Complete folder (13 files): grammar, AST, builder, SQL translator, command executor
- `Hooks/` — Complete folder (21 files): hook manager, record hook manager, 12 hook interfaces
- `Jobs/` — Complete folder (7+ files): JobManager, JobPool, JobDataService, SheduleManager, ErpBackgroundServices
- `Notifications/`, `Recurrence/`, `Fts/`, `Diagnostics/`, `Exceptions/`, `Utilities/`

**Web Layer (`WebVella.Erp.Web/`):**
- `WebVella.Erp.Web.csproj` — Project definition (net10.0 Razor SDK)
- `ErpMvcExtensions.cs` — AddErp/UseErp startup extensions
- `ErpAppContext.cs`, `ErpRequestContext.cs` — Global and per-request context
- `Controllers/` — ApiControllerBase.cs, WebApiController.cs (primary API surface)
- `Pages/` — Complete folder: 16 Razor Pages (Index, ApplicationHome, ApplicationNode, RecordCreate/Details/List/Manage, RecordRelatedRecord*, login, logout, error, Dev), layouts (_AppMaster, _SystemMaster)
- `Components/` — Complete folder: 50+ ViewComponent directories (Pc* field/layout/widget components, Nav/Menu/Hook components)
- `Services/` — Complete folder: 18 service classes (AuthService, AppService, PageService, UserService, RenderService, ThemeService, CodeEvalService, etc.)
- `Middleware/` — Complete folder: ErpMiddleware, JwtMiddleware, ErpErrorHandlingMiddleware, ErpDebugLogMiddleware, SecuritityCircuitHandler
- `Repositories/` — Complete folder: 8 Npgsql repositories
- `Models/` — Complete folder: 40+ DTOs and context models
- `TagHelpers/`, `Hooks/`, `Utils/`, `Theme/`, `Snippets/`, `wwwroot/`

**Plugin Projects:**
- `WebVella.Erp.Plugins.SDK/` — Complete folder: SdkPlugin.cs + 5 patches, Controllers/AdminController.cs, Services/, Components/, Pages/, Jobs/, wwwroot/
- `WebVella.Erp.Plugins.Next/` — Complete folder: NextPlugin.cs + 5 patches, Configuration.cs, Services/SearchService.cs, Hooks/Api/
- `WebVella.Erp.Plugins.Project/` — Complete folder: ProjectPlugin.cs + 9 patches, Controllers/, Services/, Components/, Hooks/, Jobs/, Datasource/, Files/, wwwroot/
- `WebVella.Erp.Plugins.Crm/` — Complete folder: CrmPlugin.cs + patch runner, Model/
- `WebVella.Erp.Plugins.Mail/` — Complete folder: MailPlugin.cs + 7 patches, Api/, Services/, Hooks/, Jobs/
- `WebVella.Erp.Plugins.MicrosoftCDM/` — Complete folder: MicrosoftCDMPlugin.cs + patch runner, Model/, wwwroot/

**Host Applications:**
- `WebVella.Erp.Site/` — Program.cs, Startup.cs, Config.json, web.config, JWT_README.txt
- `WebVella.Erp.Site.Sdk/`, `.Next/`, `.Project/`, `.Mail/`, `.Crm/`, `.MicrosoftCDM/` — Variant hosts (referenced, not deeply inspected — structurally identical to base Site)

**Additional Projects:**
- `WebVella.Erp.WebAssembly/` — Client/ (Blazor WASM), Server/ (hosting), Shared/ (contracts)
- `WebVella.Erp.ConsoleApp/` — Console harness
- `docs/` — Developer documentation
- `.github/` — FUNDING.yml

### 0.9.2 Technical Specification Sections Referenced

- **1.1 Executive Summary** — Project overview, version (1.7.7), core business problem, stakeholders
- **3.2 FRAMEWORKS & LIBRARIES** — Complete dependency inventory with versions for core, web, plugins
- **5.1 HIGH-LEVEL ARCHITECTURE** — System overview, core components table, data flow descriptions, external integration points

### 0.9.3 External Research Conducted

- **AWS CDK with LocalStack** — `cdklocal` wrapper usage, dual-target deployment patterns, GitHub Actions integration with `localstack/setup-localstack` action, CDK context flag for conditional resource creation
- **Nx Monorepo with React/Vite** — `@nx/vite` and `@nx/react` plugin configuration, workspace structure conventions, `tsconfig.base.json` path aliases for library imports, Vite build configuration with `nxViteTsPaths()` plugin

### 0.9.4 Attachments and External URLs

No Figma URLs or external attachments were provided for this project. The sole source of truth is the ingested WebVella ERP monolith codebase and the user's detailed rewrite specification.


