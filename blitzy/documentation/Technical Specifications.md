# Technical Specification

# 0. Agent Action Plan

## 0.1 Intent Clarification

### 0.1.1 Core Refactoring Objective

Based on the prompt, the Blitzy platform understands that the refactoring objective is to **decompose the WebVella ERP modular monolith into a suite of independently deployable, domain-aligned cloud-native microservices** while preserving 100% of existing business logic, data models, and external API contracts. The monolith currently runs as a single ASP.NET Core process backed by a shared PostgreSQL 16+ database, with all plugin modules (SDK, Mail, Project, CRM, Next, MicrosoftCDM) executing in-process through direct method calls, a hook-based event system, and PostgreSQL LISTEN/NOTIFY pub/sub.

- **Refactoring Type:** Architecture decomposition — Monolith-to-Microservices migration combined with Modularity and Tech Stack alignment
- **Target Repository:** Same repository — restructured into a multi-project solution with per-service deployable units
- **Refactoring Goals:**
  - Extract six domain-aligned microservices from the monolith: **Core/Platform**, **CRM**, **Inventory** (new boundary, currently part of Next plugin entities), **HR** (new boundary, extracted from user/role management and project user assignments), **Mail/Notifications**, and **Reporting**
  - Introduce a **Shared Kernel** library for cross-cutting contracts (DTOs, events, EQL types)
  - Replace all in-process hook-based communication with asynchronous, event-driven messaging between services
  - Migrate from the single shared PostgreSQL database to a **database-per-service** model with independent schemas
  - Establish REST and gRPC API surfaces for each service
  - Deliver a complete CI/CD pipeline configuration for containerized deployment
  - Validate deployment end-to-end using **LocalStack** inside Docker containers
  - Build a full regression test suite per service, with every extracted business rule mapping to at least one automated test
- **Implicit Requirements Surfaced:**
  - The existing EQL (Entity Query Language) engine, which generates PostgreSQL SQL with JSON-shaped results and correlated subqueries for relations, must be preserved and adapted to work within service boundaries (cross-service relation queries must be replaced with API calls or event-sourced projections)
  - The stateful singletons (`JobManager.Current`, `ScheduleManager.Current`, `ErpAppContext.Current`) must be refactored into service-scoped instances or replaced with distributed coordination
  - The in-process `IMemoryCache` with 1-hour TTL for entity metadata must be replaced with a distributed cache (Redis) or per-service local cache with event-driven invalidation
  - The `SecurityContext` using `AsyncLocal<Stack<ErpUser>>` requires conversion to a token-propagated identity model across service boundaries
  - The plugin patch/migration system (date-based versioning stored in `plugin_data`) must be adapted to per-service database migration tooling (e.g., EF Core migrations or FluentMigrator)
  - Maintain backward compatibility with the existing REST API v3 contract (`/api/v3/{locale}/...`) via an API gateway that routes requests to the appropriate microservice

### 0.1.2 Technical Interpretation

This refactoring translates to the following technical transformation strategy:

**Current Architecture → Target Architecture:**

| Aspect | Current (Monolith) | Target (Microservices) |
|--------|--------------------|-----------------------|
| Deployment | Single ASP.NET Core process | Multiple Docker containers per Kubernetes namespace |
| Communication | In-process method calls + Hook system | REST/gRPC + async event bus (RabbitMQ/SNS+SQS via LocalStack) |
| Data Store | Single PostgreSQL 16+ instance, shared `rec_*` tables | Database-per-service, each with own PostgreSQL schema |
| State Management | Stateful singletons (`JobManager.Current`, etc.) | Stateless services + distributed coordination (Redis) |
| Caching | `IMemoryCache` (1-hour TTL) | Per-service local cache + Redis for shared metadata |
| Identity | `SecurityContext` with `AsyncLocal` + cookie/JWT | JWT-only propagation across service boundaries via API Gateway |
| Background Jobs | Bounded 20-thread `JobPool` with PostgreSQL `jobs` table | Per-service hosted background workers with dedicated job tables |
| File Storage | Unified `DbFileRepository` (LO/FS/Cloud) | Shared file-storage service or S3-compatible storage via LocalStack |
| Pub/Sub | PostgreSQL LISTEN/NOTIFY on `ERP_NOTIFICATIONS_CHANNNEL` | Message broker (RabbitMQ or SNS/SQS) for inter-service events |
| EQL Engine | Cross-entity queries with relation traversal | Service-scoped EQL per service; cross-service queries via API composition |

**Transformation Rules:**
- Each monolith plugin (`WebVella.Erp.Plugins.*`) maps to a candidate microservice boundary
- Core ERP subsystems (`Api/`, `Database/`, `Eql/`, `Hooks/`, `Jobs/`) become a shared kernel library plus per-service implementations
- The Web layer (`WebVella.Erp.Web/`) remains as a UI gateway/BFF service that delegates to backend microservices
- Hook interfaces (`IErpPre/PostCreateRecordHook`, etc.) convert to domain events published on the message bus
- Each service owns its database schema and exposes its data through REST/gRPC endpoints only


## 0.2 Source Analysis

### 0.2.1 Comprehensive Source File Discovery

The WebVella ERP monolith is organized as a Visual Studio 2022/MSBuild 17 solution (`WebVella.ERP3.sln`) containing 19 projects targeting `net10.0`. The following is the complete source tree with architectural annotations indicating decomposition targets.

**Current Monolith Structure:**

```
WebVella.ERP3.sln
├── global.json                                    (SDK selection — version commented out)
├── create-nuget-pkgs.bat                         (NuGet packaging automation)
├── README.md                                      (Project landing page)
│
├── WebVella.Erp/                                  [CORE ENGINE — to become Shared Kernel + Core Service]
│   ├── WebVella.Erp.csproj                       (net10.0, v1.7.7, Apache-2.0)
│   ├── ERPService.cs                             (Bootstrap orchestrator, system entity init)
│   ├── ErpPlugin.cs                              (Abstract plugin base with JSON persistence)
│   ├── ErpSettings.cs                            (Global static config binder)
│   ├── IErpService.cs                            (Host-service lifecycle contract)
│   ├── IQueryRepository.cs                       (Marker interface for repositories)
│   ├── Api/
│   │   ├── Cache.cs                              (IMemoryCache wrapper — entities/relations)
│   │   ├── DataSourceManager.cs                  (DB + code datasource runtime)
│   │   ├── Definitions.cs                        (SystemIds, enums, GUIDs)
│   │   ├── EntityManager.cs                      (Entity/field CRUD, cache, validation)
│   │   ├── EntityRelationManager.cs              (Relation CRUD, integrity rules)
│   │   ├── ImportExportManager.cs                (CSV import/export via CsvHelper)
│   │   ├── RecordManager.cs                      (Record CRUD, hooks, permissions)
│   │   ├── SearchManager.cs                      (PostgreSQL FTS + ILIKE search)
│   │   ├── SecurityContext.cs                    (AsyncLocal user scope stack)
│   │   ├── SecurityManager.cs                    (User/role operations, credential validation)
│   │   └── Models/                               (35+ DTO/contract files)
│   ├── Database/
│   │   ├── DbContext.cs                          (Ambient context, connection stack)
│   │   ├── DbConnection.cs                       (Npgsql wrapper, savepoints, advisory locks)
│   │   ├── DbRepository.cs                       (DDL/DML helpers, index creation)
│   │   ├── DbEntityRepository.cs                 (Entity JSON persistence)
│   │   ├── DbRecordRepository.cs                 (Dynamic rec_* CRUD, query translation)
│   │   ├── DbRelationRepository.cs               (Relation FK/join table management)
│   │   ├── DbFileRepository.cs                   (File lifecycle — LO/FS/cloud)
│   │   ├── DbDataSourceRepository.cs             (Datasource CRUD)
│   │   ├── DBTypeConverter.cs                    (ERP ↔ PostgreSQL type mapping)
│   │   ├── DbSystemSettings.cs                   (System settings model)
│   │   ├── DbSystemSettingsRepository.cs          (Settings persistence)
│   │   ├── AutoMapper/                           (Mapping profiles)
│   │   └── FieldTypes/                           (Per-field-type DB implementations)
│   ├── Eql/                                       (13 files: grammar, AST, builder, SQL gen, command)
│   ├── Hooks/                                     (20 files: attributes, manager, 12 hook interfaces)
│   ├── Jobs/                                      (7 files + Models/: hosted services, pool, managers)
│   ├── Notifications/                             (5 files: LISTEN/NOTIFY pub/sub)
│   ├── Recurrence/                                (Recurrence plans via Ical.Net)
│   ├── Fts/                                       (Bulgarian FTS with embedded rules)
│   ├── Diagnostics/                               (DB-backed system_log)
│   ├── Exceptions/                                (StorageException, validation errors)
│   └── Utilities/                                 (Crypto, datetime, JSON, dynamic objects)
│
├── WebVella.Erp.Web/                              [WEB LAYER — to become API Gateway/BFF]
│   ├── WebVella.Erp.Web.csproj                   (net10.0, Razor SDK)
│   ├── ErpMvcExtensions.cs                       (AddErp/UseErp startup pipeline)
│   ├── ErpAppContext.cs                          (Singleton web context)
│   ├── ErpRequestContext.cs                      (Scoped per-request routing context)
│   ├── Controllers/
│   │   ├── ApiControllerBase.cs                  (Shared JSON response helpers)
│   │   └── WebApiController.cs                   (Major REST API v3 surface)
│   ├── Services/                                  (18 service files: auth, app, page, render, etc.)
│   ├── Middleware/                                 (6 files: ERP/JWT/error/debug middleware)
│   ├── Models/                                    (DTOs for apps/pages/components/datasources)
│   ├── Repositories/                              (Npgsql SQL repos for apps/pages/sitemap)
│   ├── Pages/                                     (Razor Pages — routable UI)
│   ├── Components/                                (Large ViewComponent library)
│   ├── TagHelpers/                                (wv-* TagHelper suite)
│   ├── Hooks/                                     (Page lifecycle hooks)
│   ├── Security/                                  (Security circuit handler)
│   └── wwwroot/                                   (Static assets)
│
├── WebVella.Erp.Plugins.SDK/                      [SDK PLUGIN — to become Admin/Platform Service]
│   ├── SdkPlugin.cs + SdkPlugin._.cs + 5 patches (Admin console, code gen, entity designer)
│   ├── Controllers/AdminController.cs             (api/v3.0/p/sdk/* endpoints)
│   ├── Services/CodeGenService.cs + LogService.cs
│   ├── Jobs/ClearJobAndErrorLogsJob.cs + SampleJob.cs
│   ├── Pages/                                     (Admin Razor Pages)
│   └── wwwroot/                                   (Stencil web components, jsTree)
│
├── WebVella.Erp.Plugins.Next/                     [NEXT PLUGIN — to become part of Core + CRM Services]
│   ├── NextPlugin.cs + NextPlugin._.cs + 5 patches (Entity provisioning, search indexing)
│   ├── Configuration.cs                           (Search index field definitions)
│   ├── Hooks/Api/                                 (Post-create/update hooks for account/case/contact/task)
│   ├── Services/SearchService.cs                  (x_search field regeneration)
│   └── Model/PluginSettings.cs
│
├── WebVella.Erp.Plugins.Project/                  [PROJECT PLUGIN — to become Project/Task Service]
│   ├── ProjectPlugin.cs + ProjectPlugin._.cs + 9 patches (Project/task management)
│   ├── Controllers/                               (api/v3.0/p/project/* endpoints)
│   ├── Services/                                  (Tasks, timelogs, comments, feed, reporting)
│   ├── Components/                                (Timesheet, task widgets, recurrence editor)
│   ├── Hooks/                                     (Entity + page lifecycle hooks)
│   ├── Jobs/StartTasksOnStartDate.cs
│   └── wwwroot/                                   (Stencil web components)
│
├── WebVella.Erp.Plugins.Mail/                     [MAIL PLUGIN — to become Mail/Notification Service]
│   ├── MailPlugin.cs + MailPlugin._.cs + 7 patches (Email entities, queue, SMTP)
│   ├── Api/                                       (DTOs, enums, AutoMapper)
│   ├── Services/                                  (SMTP engine, validation, queue processing)
│   ├── Hooks/                                     (SMTP record invariants, UI hooks)
│   └── Jobs/                                      (Scheduled queue processor)
│
├── WebVella.Erp.Plugins.Crm/                      [CRM PLUGIN — to become CRM Service]
│   ├── CrmPlugin.cs + CrmPlugin._.cs             (CRM skeleton with patch framework)
│   └── Model/PluginSettings.cs
│
├── WebVella.Erp.Plugins.MicrosoftCDM/             [CDM PLUGIN — to become part of Core Service]
│   ├── MicrosoftCDMPlugin.cs + MicrosoftCDMPlugin._.cs
│   └── wwwroot/emtpy.txt
│
├── WebVella.Erp.Site/                             [BASE SITE HOST — composition root reference]
│   ├── Config.json, Program.cs, Startup.cs        (Cookie+JWT auth, middleware pipeline)
│   └── WebVella.Erp.Site.csproj                   (Wires SDK plugin only)
│
├── WebVella.Erp.Site.Sdk/                         [VARIANT HOST — SDK-only]
├── WebVella.Erp.Site.Next/                        [VARIANT HOST — Next+SDK]
├── WebVella.Erp.Site.Project/                     [VARIANT HOST — Next+Project+SDK]
├── WebVella.Erp.Site.Mail/                        [VARIANT HOST — Next+Mail+SDK]
├── WebVella.Erp.Site.Crm/                         [VARIANT HOST — Next+CRM+SDK]
├── WebVella.Erp.Site.MicrosoftCDM/                [VARIANT HOST — CDM+SDK]
│
├── WebVella.Erp.ConsoleApp/                       [CONSOLE HARNESS — reference for service bootstrap]
│   ├── Program.cs                                 (ERP init + EQL demo + hook demo)
│   └── RoleRecordHooks.cs + UserRecordHooks.cs
│
└── WebVella.Erp.WebAssembly/                      [BLAZOR WASM CLIENT — out of scope for UI redesign]
    ├── Client/                                     (Blazor WASM SPA, net10.0)
    ├── Server/                                     (ASP.NET Core host, net7.0)
    └── Shared/                                     (Placeholder shared lib, net7.0)
```

### 0.2.2 Source File Inventory

All source files requiring refactoring are comprehensively listed below, grouped by monolith project.

**Core Engine — `WebVella.Erp/` (estimated 80+ files):**

| File/Folder | Lines (est.) | Refactoring Role |
|-------------|-------------|-----------------|
| `ERPService.cs` | 1400+ | Split: Core bootstrap vs per-service init |
| `ErpPlugin.cs` | 200+ | Refactor: Remove plugin model, extract to service contracts |
| `ErpSettings.cs` | 300+ | Split: Shared config vs per-service settings |
| `Api/RecordManager.cs` | 1500+ | Split: Per-service record managers |
| `Api/EntityManager.cs` | 1200+ | Move to Core service, expose via gRPC |
| `Api/SecurityManager.cs` | 500+ | Move to Identity/Auth service |
| `Api/SecurityContext.cs` | 200+ | Replace with JWT propagation |
| `Api/DataSourceManager.cs` | 800+ | Per-service datasource runtime |
| `Api/SearchManager.cs` | 300+ | Move to Core or Search service |
| `Api/Cache.cs` | 100+ | Replace with distributed cache |
| `Api/ImportExportManager.cs` | 600+ | Move to Core service |
| `Database/DbContext.cs` | 300+ | Per-service DbContext |
| `Database/DbRecordRepository.cs` | 2000+ | Per-service repository |
| `Database/DbEntityRepository.cs` | 800+ | Core service only |
| `Database/DbRelationRepository.cs` | 600+ | Per-service as needed |
| `Database/DbFileRepository.cs` | 500+ | File storage service |
| `Database/DbRepository.cs` | 1500+ | Shared library |
| `Eql/` (13 files) | 3000+ | Per-service EQL with scoped entities |
| `Hooks/` (20 files) | 1000+ | Replace with event contracts |
| `Jobs/` (7+ files) | 2000+ | Per-service background workers |
| `Notifications/` (5 files) | 400+ | Replace with message broker |

**Web Layer — `WebVella.Erp.Web/` (estimated 100+ files):**
- `Controllers/WebApiController.cs` — Major API surface to be decomposed across services
- `Services/` (18 files) — Each service maps to a microservice client proxy
- `Middleware/` (6 files) — API Gateway middleware layer

**Plugins — All 6 plugin projects:**
- Each plugin's `*Plugin.cs`, `*Plugin._.cs`, and patch files contain schema migrations
- Each plugin's `Services/`, `Controllers/`, `Hooks/`, `Jobs/` folders contain domain logic

**Site Hosts — All 7 site projects:**
- Each `Startup.cs` and `Config.json` contain composition wiring to be replaced by per-service hosting

**No existing test projects** were found in the repository — the monolith has zero automated tests, making the requirement for a full regression test suite per service an entirely new effort.


## 0.3 Scope Boundaries

### 0.3.1 Exhaustively In Scope

**Service Boundary Extraction (all source transformations):**
- `WebVella.Erp/**/*.cs` — Core engine decomposition into Shared Kernel and Core Platform service
- `WebVella.Erp.Web/**/*.cs` — API Gateway/BFF transformation
- `WebVella.Erp.Plugins.SDK/**/*.cs` — Admin/Platform service extraction
- `WebVella.Erp.Plugins.Next/**/*.cs` — Core entities + CRM search indexing split
- `WebVella.Erp.Plugins.Project/**/*.cs` — Project/Task microservice extraction
- `WebVella.Erp.Plugins.Mail/**/*.cs` — Mail/Notification microservice extraction
- `WebVella.Erp.Plugins.Crm/**/*.cs` — CRM microservice extraction
- `WebVella.Erp.Plugins.MicrosoftCDM/**/*.cs` — Merge into Core Platform service
- `WebVella.Erp.ConsoleApp/**/*.cs` — Reference for service bootstrap patterns

**REST/gRPC API surface per service:**
- New `.proto` files for gRPC service definitions per domain
- New REST controller files per service following the existing `/api/v3/` contract patterns
- API Gateway routing configuration mapping legacy endpoints to microservices

**Event-driven communication:**
- New event contract definitions for all current hook interfaces (12 hook contracts in `WebVella.Erp/Hooks/`)
- Message broker integration (RabbitMQ for local/Docker, SNS+SQS for LocalStack validation)
- Event publisher/subscriber implementations per service

**Database-per-service migration:**
- Schema migration scripts per service extracting relevant `rec_*`, `rel_*`, and system tables
- Data migration tooling (EF Core Migrations or FluentMigrator) per service
- Cross-reference resolution for entities that span service boundaries

**Test suite creation (entirely new):**
- `tests/**/*.cs` — New test projects per service
- Unit tests for all business logic classes
- Integration tests for API endpoints
- Cross-service integration tests validating business rules
- Schema migration tests ensuring zero data loss

**CI/CD pipeline configuration:**
- `docker-compose*.yml` — Multi-service Docker Compose definitions
- `Dockerfile` per service — Containerization
- `.github/workflows/*.yml` — GitHub Actions CI/CD pipelines
- LocalStack configuration files for deployment validation

**Configuration and infrastructure:**
- Per-service `appsettings.json` replacing monolith `Config.json`
- Docker/Kubernetes manifest files (Helm charts or raw YAML)
- Shared infrastructure configuration (Redis, RabbitMQ, API Gateway)

**Documentation updates:**
- `README.md` — Updated repository structure and setup instructions
- Per-service `README.md` files
- API documentation per service (OpenAPI/Swagger)

**Import corrections:**
- Every `.cs` file containing `using WebVella.Erp.*` namespaces must be updated to reference new project/namespace structure
- All `ProjectReference` entries in `.csproj` files must be updated

### 0.3.2 Explicitly Out of Scope

Per the user's explicit instructions, the following are **out of scope:**

| Out of Scope Item | Rationale |
|-------------------|-----------|
| New business features not in the current monolith | User directive — preserve only existing functionality |
| UI redesign | User directive — `WebVella.Erp.Web/Components/`, `Pages/`, `TagHelpers/`, `wwwroot/` remain structurally unchanged as a gateway/BFF |
| Third-party ERP integrations not already present | User directive — only existing integrations (PostgreSQL, SMTP via MailKit, Storage.Net cloud backends) |
| `WebVella.Erp.WebAssembly/` Blazor WASM upgrade | Server/Shared projects target `net7.0`; upgrading the Blazor client is a separate effort |
| Performance optimization beyond refactoring | No changes to EQL timeout (600s), job pool size (20), or cache TTL (1 hour) unless required for microservice operation |
| Data archival implementation | Not present in monolith; adding it would be a new feature |
| PostgreSQL replication or read/write splitting | Infrastructure-level concern outside application refactoring |


## 0.4 Target Design

### 0.4.1 Refactored Structure Planning

The target architecture decomposes the monolith into seven independently deployable services plus shared infrastructure. Every file and folder is explicitly specified below.

```
webvella-erp-microservices/
├── README.md
├── WebVella.ERP3.sln                              (Updated solution with all new projects)
├── global.json                                     (Pin .NET 10 SDK version)
├── Directory.Build.props                           (Centralized package versions, shared properties)
├── Directory.Packages.props                        (Central Package Management)
├── docker-compose.yml                              (Full orchestration: all services + infra)
├── docker-compose.localstack.yml                   (LocalStack-specific overrides for validation)
├── docker-compose.override.yml                     (Development overrides)
│
├── src/
│   ├── SharedKernel/
│   │   └── WebVella.Erp.SharedKernel/
│   │       ├── WebVella.Erp.SharedKernel.csproj
│   │       ├── Contracts/
│   │       │   ├── Events/                         (Domain event base types and interfaces)
│   │       │   ├── Commands/                       (Shared command contracts)
│   │       │   └── Queries/                        (Shared query contracts)
│   │       ├── Models/
│   │       │   ├── EntityRecord.cs                 (Preserved from Api/Models/)
│   │       │   ├── EntityRecordList.cs
│   │       │   ├── BaseResponseModel.cs
│   │       │   ├── ErrorModel.cs
│   │       │   ├── ErpUser.cs
│   │       │   ├── ErpRole.cs
│   │       │   └── SystemIds.cs                    (Well-known GUIDs)
│   │       ├── Eql/                                (EQL grammar, AST, builder — shared engine)
│   │       ├── Database/
│   │       │   ├── DbRepository.cs                 (DDL/DML helpers, shared)
│   │       │   ├── DbConnection.cs                 (Connection wrapper, savepoints)
│   │       │   ├── DBTypeConverter.cs              (Type mapping)
│   │       │   └── DbParameter.cs
│   │       ├── Security/
│   │       │   ├── SecurityContext.cs              (Adapted for JWT propagation)
│   │       │   └── JwtTokenHandler.cs              (Shared JWT creation/validation)
│   │       ├── Utilities/                           (Crypto, datetime, JSON helpers)
│   │       ├── Exceptions/                          (StorageException, ValidationException)
│   │       └── Fts/                                 (Bulgarian FTS rules)
│   │
│   ├── Services/
│   │   ├── WebVella.Erp.Service.Core/              [CORE PLATFORM SERVICE]
│   │   │   ├── WebVella.Erp.Service.Core.csproj
│   │   │   ├── Dockerfile
│   │   │   ├── appsettings.json
│   │   │   ├── Program.cs                          (Minimal hosting API)
│   │   │   ├── Api/
│   │   │   │   ├── EntityManager.cs
│   │   │   │   ├── EntityRelationManager.cs
│   │   │   │   ├── RecordManager.cs
│   │   │   │   ├── SecurityManager.cs
│   │   │   │   ├── DataSourceManager.cs
│   │   │   │   ├── SearchManager.cs
│   │   │   │   ├── ImportExportManager.cs
│   │   │   │   └── Cache.cs
│   │   │   ├── Database/
│   │   │   │   ├── CoreDbContext.cs
│   │   │   │   ├── DbEntityRepository.cs
│   │   │   │   ├── DbRecordRepository.cs
│   │   │   │   ├── DbRelationRepository.cs
│   │   │   │   ├── DbFileRepository.cs
│   │   │   │   ├── DbDataSourceRepository.cs
│   │   │   │   ├── DbSystemSettingsRepository.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Controllers/
│   │   │   │   ├── EntityController.cs
│   │   │   │   ├── RecordController.cs
│   │   │   │   ├── SecurityController.cs
│   │   │   │   ├── FileController.cs
│   │   │   │   ├── DataSourceController.cs
│   │   │   │   └── SearchController.cs
│   │   │   ├── Grpc/
│   │   │   │   ├── EntityGrpcService.cs
│   │   │   │   ├── RecordGrpcService.cs
│   │   │   │   └── SecurityGrpcService.cs
│   │   │   ├── Events/
│   │   │   │   ├── Publishers/
│   │   │   │   └── Subscribers/
│   │   │   ├── Jobs/
│   │   │   │   ├── JobManager.cs
│   │   │   │   ├── JobPool.cs
│   │   │   │   └── ScheduleManager.cs
│   │   │   └── Diagnostics/
│   │   │
│   │   ├── WebVella.Erp.Service.Crm/              [CRM SERVICE]
│   │   │   ├── WebVella.Erp.Service.Crm.csproj
│   │   │   ├── Dockerfile
│   │   │   ├── appsettings.json
│   │   │   ├── Program.cs
│   │   │   ├── Domain/
│   │   │   │   ├── Entities/                       (account, contact, case, address, salutation)
│   │   │   │   └── Services/
│   │   │   │       └── SearchService.cs            (x_search regeneration)
│   │   │   ├── Database/
│   │   │   │   ├── CrmDbContext.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Controllers/
│   │   │   │   └── CrmController.cs
│   │   │   ├── Grpc/
│   │   │   │   └── CrmGrpcService.cs
│   │   │   ├── Events/
│   │   │   │   ├── Publishers/
│   │   │   │   └── Subscribers/
│   │   │   └── Patches/                            (Migrated from CrmPlugin + NextPlugin CRM entities)
│   │   │
│   │   ├── WebVella.Erp.Service.Project/           [PROJECT/TASK SERVICE]
│   │   │   ├── WebVella.Erp.Service.Project.csproj
│   │   │   ├── Dockerfile
│   │   │   ├── appsettings.json
│   │   │   ├── Program.cs
│   │   │   ├── Domain/
│   │   │   │   ├── Entities/                       (task, timelog, comment, feed)
│   │   │   │   └── Services/
│   │   │   │       ├── TaskService.cs
│   │   │   │       ├── TimelogService.cs
│   │   │   │       ├── CommentService.cs
│   │   │   │       ├── FeedService.cs
│   │   │   │       └── ReportingService.cs
│   │   │   ├── Database/
│   │   │   │   ├── ProjectDbContext.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Controllers/
│   │   │   │   └── ProjectController.cs
│   │   │   ├── Grpc/
│   │   │   │   └── ProjectGrpcService.cs
│   │   │   ├── Events/
│   │   │   ├── Jobs/
│   │   │   │   └── StartTasksOnStartDateJob.cs
│   │   │   └── Patches/
│   │   │
│   │   ├── WebVella.Erp.Service.Mail/              [MAIL/NOTIFICATION SERVICE]
│   │   │   ├── WebVella.Erp.Service.Mail.csproj
│   │   │   ├── Dockerfile
│   │   │   ├── appsettings.json
│   │   │   ├── Program.cs
│   │   │   ├── Domain/
│   │   │   │   ├── Entities/                       (email, smtp_service)
│   │   │   │   └── Services/
│   │   │   │       └── SmtpService.cs              (Send, queue, validation)
│   │   │   ├── Database/
│   │   │   │   ├── MailDbContext.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Controllers/
│   │   │   │   └── MailController.cs
│   │   │   ├── Grpc/
│   │   │   │   └── MailGrpcService.cs
│   │   │   ├── Events/
│   │   │   ├── Jobs/
│   │   │   │   └── ProcessMailQueueJob.cs
│   │   │   └── Patches/
│   │   │
│   │   ├── WebVella.Erp.Service.Reporting/         [REPORTING SERVICE]
│   │   │   ├── WebVella.Erp.Service.Reporting.csproj
│   │   │   ├── Dockerfile
│   │   │   ├── appsettings.json
│   │   │   ├── Program.cs
│   │   │   ├── Domain/
│   │   │   │   └── Services/
│   │   │   │       └── ReportAggregationService.cs
│   │   │   ├── Database/
│   │   │   │   ├── ReportingDbContext.cs
│   │   │   │   └── Migrations/
│   │   │   ├── Controllers/
│   │   │   │   └── ReportController.cs
│   │   │   └── Events/
│   │   │
│   │   └── WebVella.Erp.Service.Admin/             [ADMIN/SDK SERVICE]
│   │       ├── WebVella.Erp.Service.Admin.csproj
│   │       ├── Dockerfile
│   │       ├── appsettings.json
│   │       ├── Program.cs
│   │       ├── Services/
│   │       │   ├── CodeGenService.cs
│   │       │   └── LogService.cs
│   │       ├── Controllers/
│   │       │   └── AdminController.cs
│   │       ├── Jobs/
│   │       │   └── ClearJobAndErrorLogsJob.cs
│   │       ├── Database/
│   │       │   ├── AdminDbContext.cs
│   │       │   └── Migrations/
│   │       └── Patches/
│   │
│   └── Gateway/
│       └── WebVella.Erp.Gateway/                   [API GATEWAY / BFF]
│           ├── WebVella.Erp.Gateway.csproj
│           ├── Dockerfile
│           ├── appsettings.json
│           ├── Program.cs
│           ├── Middleware/
│           │   ├── AuthenticationMiddleware.cs
│           │   ├── ErrorHandlingMiddleware.cs
│           │   └── RequestRoutingMiddleware.cs
│           ├── Configuration/
│           │   └── RouteConfiguration.cs
│           └── Pages/                               (Preserved Razor Pages from Web layer)
│
├── proto/                                           [gRPC PROTO DEFINITIONS]
│   ├── core.proto
│   ├── crm.proto
│   ├── project.proto
│   ├── mail.proto
│   ├── reporting.proto
│   └── admin.proto
│
├── tests/
│   ├── WebVella.Erp.Tests.SharedKernel/
│   ├── WebVella.Erp.Tests.Core/
│   ├── WebVella.Erp.Tests.Crm/
│   ├── WebVella.Erp.Tests.Project/
│   ├── WebVella.Erp.Tests.Mail/
│   ├── WebVella.Erp.Tests.Reporting/
│   ├── WebVella.Erp.Tests.Admin/
│   ├── WebVella.Erp.Tests.Gateway/
│   └── WebVella.Erp.Tests.Integration/             (Cross-service business rule validation)
│
├── infrastructure/
│   ├── localstack/
│   │   ├── init-aws.sh                             (LocalStack resource provisioning)
│   │   └── localstack-config.yml
│   ├── kubernetes/
│   │   ├── namespace.yml
│   │   ├── core-deployment.yml
│   │   ├── crm-deployment.yml
│   │   ├── project-deployment.yml
│   │   ├── mail-deployment.yml
│   │   ├── reporting-deployment.yml
│   │   ├── admin-deployment.yml
│   │   ├── gateway-deployment.yml
│   │   └── services.yml
│   └── scripts/
│       ├── migrate-data.sh
│       └── validate-deployment.sh
│
└── .github/
    └── workflows/
        ├── ci.yml                                   (Build + test all services)
        ├── cd.yml                                   (Deploy to Docker/K8s)
        └── localstack-validation.yml               (E2E with LocalStack)
```

### 0.4.2 Web Search Research Conducted

- **.NET 10 (latest stable release):** Confirmed as the target runtime — .NET 10 was released November 2025 as an LTS version supported until November 2028. The project already targets `net10.0` with latest packages (10.0.1). No framework upgrade is required; the target is already aligned.
- **LocalStack for .NET microservices:** LocalStack runs as a single Docker container emulating AWS services (SQS, SNS, S3, etc.) on port 4566. The `Testcontainers.LocalStack` NuGet package (v4.10.0) provides programmatic container management for integration tests. Docker Compose is the recommended orchestration tool for multi-service + LocalStack stacks.
- **Database-per-service migration strategy:** Schema migration for each service requires extracting relevant `rec_*` tables, `rel_*` join tables, and supporting system tables into service-specific PostgreSQL databases. Data migration scripts must handle foreign key references that cross service boundaries by denormalizing or using eventual consistency.
- **Event-driven patterns for .NET:** MassTransit or raw RabbitMQ client libraries are the standard choices for .NET microservice messaging. For LocalStack validation, SNS topics + SQS queues provide AWS-compatible event routing.

### 0.4.3 Design Pattern Applications

| Pattern | Application in Target Architecture |
|---------|-----------------------------------|
| **Database-per-Service** | Each microservice owns an independent PostgreSQL database instance |
| **API Gateway** | Single entry point routing to backend services, preserving `/api/v3/` contracts |
| **Event-Driven Architecture** | Hook-based synchronous communication replaced by async domain events |
| **Saga Pattern** | Cross-service transactions (e.g., CRM + Project linkage) use choreography-based sagas |
| **CQRS (light)** | Reporting service reads event-sourced projections; write services own the commands |
| **Strangler Fig** | Gateway routes requests to either legacy monolith endpoints or new microservices during migration |
| **Shared Kernel** | Common DTOs, EQL engine, security contracts shared across services |
| **Repository Pattern** | Preserved from monolith, scoped to per-service data access |
| **Sidecar / Ambassador** | Per-service health check, metrics export, and configuration loading |


## 0.5 Transformation Mapping

### 0.5.1 File-by-File Transformation Plan

The complete transformation map below lists every target file, its transformation mode, the source file it derives from, and the key changes required. This plan covers the entire refactoring in **one phase**.

**Shared Kernel:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/SharedKernel/WebVella.Erp.SharedKernel/WebVella.Erp.SharedKernel.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | New class library extracting shared types, remove non-shared deps |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/EntityRecord.cs` | UPDATE | `WebVella.Erp/Api/Models/EntityRecord.cs` | Move to shared; retain Expando-based dynamic record type |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/EntityRecordList.cs` | UPDATE | `WebVella.Erp/Api/Models/EntityRecordList.cs` | Move to shared kernel |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/BaseResponseModel.cs` | UPDATE | `WebVella.Erp/Api/Models/BaseModels.cs` | Extract base response envelope types |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/ErpUser.cs` | UPDATE | `WebVella.Erp/Api/Models/ErpUser.cs` | Move to shared; add cross-service identity claims |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/ErpRole.cs` | UPDATE | `WebVella.Erp/Api/Models/ErpRole.cs` | Move to shared kernel |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Models/SystemIds.cs` | UPDATE | `WebVella.Erp/Api/Definitions.cs` | Extract SystemIds and shared enums |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Contracts/Events/*.cs` | CREATE | `WebVella.Erp/Hooks/IErp*Hook.cs` | Convert 12 hook interfaces to domain event contracts |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Eql/*.cs` | UPDATE | `WebVella.Erp/Eql/*.cs` | Move entire EQL engine (13 files) to shared; remove service-specific references |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Database/DbRepository.cs` | UPDATE | `WebVella.Erp/Database/DbRepository.cs` | Extract shared DDL/DML helpers |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Database/DbConnection.cs` | UPDATE | `WebVella.Erp/Database/DbConnection.cs` | Move connection wrapper to shared |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Database/DBTypeConverter.cs` | UPDATE | `WebVella.Erp/Database/DBTypeConverter.cs` | Move type mapping to shared |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Security/SecurityContext.cs` | UPDATE | `WebVella.Erp/Api/SecurityContext.cs` | Adapt for JWT token propagation across services |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Security/JwtTokenHandler.cs` | CREATE | `WebVella.Erp.Web/Services/AuthService.cs` | Extract shared JWT creation/validation logic |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Utilities/*.cs` | UPDATE | `WebVella.Erp/Utilities/*.cs` | Move all cross-cutting helpers |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Exceptions/*.cs` | UPDATE | `WebVella.Erp/Exceptions/*.cs` | Move exception types |
| `src/SharedKernel/WebVella.Erp.SharedKernel/Fts/**/*.cs` | UPDATE | `WebVella.Erp/Fts/**/*.cs` | Move FTS with embedded rules |

**Core Platform Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/Services/WebVella.Erp.Service.Core/WebVella.Erp.Service.Core.csproj` | CREATE | `WebVella.Erp/WebVella.Erp.csproj` | New web API project referencing SharedKernel |
| `src/Services/WebVella.Erp.Service.Core/Dockerfile` | CREATE | — | Multi-stage build targeting `mcr.microsoft.com/dotnet/aspnet:10.0` |
| `src/Services/WebVella.Erp.Service.Core/Program.cs` | CREATE | `WebVella.Erp.Site/Program.cs` | Minimal hosting API with per-service DI |
| `src/Services/WebVella.Erp.Service.Core/appsettings.json` | CREATE | `WebVella.Erp.Site/Config.json` | Per-service settings (own DB connection, JWT config) |
| `src/Services/WebVella.Erp.Service.Core/Api/EntityManager.cs` | UPDATE | `WebVella.Erp/Api/EntityManager.cs` | Scoped to core entities, expose via gRPC |
| `src/Services/WebVella.Erp.Service.Core/Api/RecordManager.cs` | UPDATE | `WebVella.Erp/Api/RecordManager.cs` | Core-only record operations, event publishing on CRUD |
| `src/Services/WebVella.Erp.Service.Core/Api/SecurityManager.cs` | UPDATE | `WebVella.Erp/Api/SecurityManager.cs` | Identity/auth service for users and roles |
| `src/Services/WebVella.Erp.Service.Core/Api/DataSourceManager.cs` | UPDATE | `WebVella.Erp/Api/DataSourceManager.cs` | Core datasource runtime |
| `src/Services/WebVella.Erp.Service.Core/Api/SearchManager.cs` | UPDATE | `WebVella.Erp/Api/SearchManager.cs` | Core search across owned entities |
| `src/Services/WebVella.Erp.Service.Core/Api/ImportExportManager.cs` | UPDATE | `WebVella.Erp/Api/ImportExportManager.cs` | CSV import/export for core entities |
| `src/Services/WebVella.Erp.Service.Core/Api/Cache.cs` | UPDATE | `WebVella.Erp/Api/Cache.cs` | Replace IMemoryCache with Redis for distributed caching |
| `src/Services/WebVella.Erp.Service.Core/Database/CoreDbContext.cs` | CREATE | `WebVella.Erp/Database/DbContext.cs` | Per-service ambient context |
| `src/Services/WebVella.Erp.Service.Core/Database/DbEntityRepository.cs` | UPDATE | `WebVella.Erp/Database/DbEntityRepository.cs` | Scoped to core service DB |
| `src/Services/WebVella.Erp.Service.Core/Database/DbRecordRepository.cs` | UPDATE | `WebVella.Erp/Database/DbRecordRepository.cs` | Scoped to core rec_* tables |
| `src/Services/WebVella.Erp.Service.Core/Database/DbRelationRepository.cs` | UPDATE | `WebVella.Erp/Database/DbRelationRepository.cs` | Core-only relations |
| `src/Services/WebVella.Erp.Service.Core/Database/DbFileRepository.cs` | UPDATE | `WebVella.Erp/Database/DbFileRepository.cs` | File storage management |
| `src/Services/WebVella.Erp.Service.Core/Controllers/*.cs` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | Split REST endpoints by domain (entity, record, security, file, search) |
| `src/Services/WebVella.Erp.Service.Core/Grpc/*.cs` | CREATE | — | New gRPC service implementations for inter-service calls |
| `src/Services/WebVella.Erp.Service.Core/Events/**/*.cs` | CREATE | `WebVella.Erp/Hooks/RecordHookManager.cs` | Event publishers replacing hook execution |
| `src/Services/WebVella.Erp.Service.Core/Jobs/*.cs` | UPDATE | `WebVella.Erp/Jobs/*.cs` | Service-scoped job system |

**CRM Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/Services/WebVella.Erp.Service.Crm/WebVella.Erp.Service.Crm.csproj` | CREATE | `WebVella.Erp.Plugins.Crm/WebVella.Erp.Plugins.Crm.csproj` | Standalone API project |
| `src/Services/WebVella.Erp.Service.Crm/Dockerfile` | CREATE | — | Service container image |
| `src/Services/WebVella.Erp.Service.Crm/Program.cs` | CREATE | `WebVella.Erp.Site.Crm/Startup.cs` | Minimal hosting with CRM DI |
| `src/Services/WebVella.Erp.Service.Crm/appsettings.json` | CREATE | `WebVella.Erp.Site.Crm/Config.json` | CRM-specific DB and messaging config |
| `src/Services/WebVella.Erp.Service.Crm/Domain/Entities/*.cs` | CREATE | `WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs` | Extract account/contact/case/address entity definitions |
| `src/Services/WebVella.Erp.Service.Crm/Domain/Services/SearchService.cs` | UPDATE | `WebVella.Erp.Plugins.Next/Services/SearchService.cs` | CRM search indexing |
| `src/Services/WebVella.Erp.Service.Crm/Database/CrmDbContext.cs` | CREATE | `WebVella.Erp/Database/DbContext.cs` | CRM-specific database context |
| `src/Services/WebVella.Erp.Service.Crm/Controllers/CrmController.cs` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | CRM REST endpoints |
| `src/Services/WebVella.Erp.Service.Crm/Grpc/CrmGrpcService.cs` | CREATE | — | CRM inter-service gRPC |
| `src/Services/WebVella.Erp.Service.Crm/Patches/*.cs` | UPDATE | `WebVella.Erp.Plugins.Crm/CrmPlugin._.cs` | Convert patch system to EF migrations |
| `src/Services/WebVella.Erp.Service.Crm/Events/**/*.cs` | CREATE | `WebVella.Erp.Plugins.Next/Hooks/Api/*.cs` | Convert hooks to event publishers/subscribers |

**Project/Task Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/Services/WebVella.Erp.Service.Project/WebVella.Erp.Service.Project.csproj` | CREATE | `WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj` | Standalone API project |
| `src/Services/WebVella.Erp.Service.Project/Dockerfile` | CREATE | — | Service container image |
| `src/Services/WebVella.Erp.Service.Project/Program.cs` | CREATE | `WebVella.Erp.Site.Project/Startup.cs` | Project service hosting |
| `src/Services/WebVella.Erp.Service.Project/appsettings.json` | CREATE | `WebVella.Erp.Site.Project/Config.json` | Project-specific configuration |
| `src/Services/WebVella.Erp.Service.Project/Domain/Services/TaskService.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Services/*.cs` | Extract task domain service |
| `src/Services/WebVella.Erp.Service.Project/Domain/Services/TimelogService.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Services/*.cs` | Extract timelog service |
| `src/Services/WebVella.Erp.Service.Project/Domain/Services/CommentService.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Services/*.cs` | Extract comment service |
| `src/Services/WebVella.Erp.Service.Project/Domain/Services/FeedService.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Services/*.cs` | Extract feed service |
| `src/Services/WebVella.Erp.Service.Project/Controllers/ProjectController.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Controllers/*.cs` | REST endpoints for project CRUD |
| `src/Services/WebVella.Erp.Service.Project/Jobs/StartTasksOnStartDateJob.cs` | UPDATE | `WebVella.Erp.Plugins.Project/Jobs/StartTasksOnStartDate.cs` | Service-scoped scheduled job |
| `src/Services/WebVella.Erp.Service.Project/Patches/*.cs` | UPDATE | `WebVella.Erp.Plugins.Project/ProjectPlugin.*.cs` | Convert to EF Core migrations |

**Mail/Notification Service:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/Services/WebVella.Erp.Service.Mail/WebVella.Erp.Service.Mail.csproj` | CREATE | `WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj` | Standalone API project with MailKit |
| `src/Services/WebVella.Erp.Service.Mail/Dockerfile` | CREATE | — | Service container image |
| `src/Services/WebVella.Erp.Service.Mail/Program.cs` | CREATE | `WebVella.Erp.Site.Mail/Startup.cs` | Mail service hosting |
| `src/Services/WebVella.Erp.Service.Mail/appsettings.json` | CREATE | `WebVella.Erp.Site.Mail/Config.json` | Mail-specific settings |
| `src/Services/WebVella.Erp.Service.Mail/Domain/Services/SmtpService.cs` | UPDATE | `WebVella.Erp.Plugins.Mail/Services/*.cs` | Full SMTP engine preservation |
| `src/Services/WebVella.Erp.Service.Mail/Controllers/MailController.cs` | CREATE | `WebVella.Erp.Web/Controllers/WebApiController.cs` | Mail REST API |
| `src/Services/WebVella.Erp.Service.Mail/Jobs/ProcessMailQueueJob.cs` | UPDATE | `WebVella.Erp.Plugins.Mail/Jobs/*.cs` | Scheduled queue processor |
| `src/Services/WebVella.Erp.Service.Mail/Patches/*.cs` | UPDATE | `WebVella.Erp.Plugins.Mail/MailPlugin.*.cs` | Convert to EF migrations |

**Reporting, Admin, and Gateway Services:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `src/Services/WebVella.Erp.Service.Reporting/**/*.cs` | CREATE | `WebVella.Erp.Plugins.Project/Services/*Reporting*` | Aggregation service consuming events |
| `src/Services/WebVella.Erp.Service.Admin/**/*.cs` | UPDATE | `WebVella.Erp.Plugins.SDK/**/*.cs` | SDK plugin becomes standalone admin service |
| `src/Gateway/WebVella.Erp.Gateway/**/*.cs` | CREATE | `WebVella.Erp.Web/**/*.cs` | API Gateway routing, auth middleware, Razor Pages BFF |

**Infrastructure Files:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `docker-compose.yml` | CREATE | — | Full orchestration of all services + PostgreSQL + Redis + RabbitMQ |
| `docker-compose.localstack.yml` | CREATE | — | LocalStack overlay (SNS, SQS, S3 emulation) |
| `infrastructure/localstack/init-aws.sh` | CREATE | — | Provision SNS topics, SQS queues, S3 buckets in LocalStack |
| `infrastructure/kubernetes/*.yml` | CREATE | — | K8s deployment/service manifests per microservice |
| `.github/workflows/ci.yml` | CREATE | — | Build, test, and lint all services |
| `.github/workflows/cd.yml` | CREATE | — | Docker build, push, deploy |
| `.github/workflows/localstack-validation.yml` | CREATE | — | E2E tests against LocalStack stack |
| `proto/*.proto` | CREATE | — | gRPC service definitions per domain |
| `Directory.Build.props` | CREATE | — | Centralized build properties |
| `Directory.Packages.props` | CREATE | — | Central Package Management for all projects |
| `global.json` | UPDATE | `global.json` | Pin .NET 10 SDK version explicitly |

**Test Projects:**

| Target File | Transformation | Source File | Key Changes |
|-------------|---------------|-------------|-------------|
| `tests/WebVella.Erp.Tests.Core/**/*.cs` | CREATE | `WebVella.Erp/Api/*.cs` | Unit + integration tests for core managers |
| `tests/WebVella.Erp.Tests.Crm/**/*.cs` | CREATE | `WebVella.Erp.Plugins.Crm/**/*.cs` | CRM business rule regression tests |
| `tests/WebVella.Erp.Tests.Project/**/*.cs` | CREATE | `WebVella.Erp.Plugins.Project/**/*.cs` | Project service tests |
| `tests/WebVella.Erp.Tests.Mail/**/*.cs` | CREATE | `WebVella.Erp.Plugins.Mail/**/*.cs` | Mail service tests |
| `tests/WebVella.Erp.Tests.Integration/**/*.cs` | CREATE | — | Cross-service integration tests with Testcontainers.LocalStack |

### 0.5.2 Cross-File Dependencies

**Import Statement Transformations:**

| Current Import | New Import | Affected Files |
|---------------|------------|----------------|
| `using WebVella.Erp.Api` | `using WebVella.Erp.SharedKernel.Models` | All service projects |
| `using WebVella.Erp.Api.Models` | `using WebVella.Erp.SharedKernel.Models` | All service projects |
| `using WebVella.Erp.Database` | `using WebVella.Erp.SharedKernel.Database` (shared) OR `using WebVella.Erp.Service.{Name}.Database` (service-specific) | Per-service files |
| `using WebVella.Erp.Eql` | `using WebVella.Erp.SharedKernel.Eql` | All services using EQL |
| `using WebVella.Erp.Hooks` | `using WebVella.Erp.SharedKernel.Contracts.Events` | All event publisher/subscriber files |
| `using WebVella.Erp.Jobs` | `using WebVella.Erp.Service.{Name}.Jobs` | Per-service job files |
| `using WebVella.Erp.Notifications` | `using WebVella.Erp.SharedKernel.Contracts.Events` | Replace with message broker integration |
| `using WebVella.Erp.Utilities` | `using WebVella.Erp.SharedKernel.Utilities` | All service projects |
| `using WebVella.Erp.Web.Services` | Service-specific client proxies via HttpClient/gRPC | Gateway and inter-service calls |

**Configuration Updates:**

| Configuration | Current Location | Target Location |
|--------------|-----------------|-----------------|
| Database connection string | `Config.json → Settings:ConnectionString` | Per-service `appsettings.json → ConnectionStrings:Default` |
| JWT settings | `Config.json → Settings:Jwt:*` | Shared via environment variables or centralized config |
| Plugin data persistence | `plugin_data` table | Per-service EF Core migrations |
| Background jobs toggle | `Config.json → Settings:EnableBackgroungJobs` | Per-service `appsettings.json → Jobs:Enabled` |
| File storage config | `Config.json → Settings:EnableFileSystemStorage` | Core service `appsettings.json → Storage:*` |

### 0.5.3 One-Phase Execution

The entire refactoring is executed by Blitzy in **one phase**. All files listed above — shared kernel, seven services, gateway, infrastructure, tests, CI/CD — are created or modified simultaneously. There is no phased rollout or incremental migration; the full microservice architecture is delivered as a single coherent transformation.


## 0.6 Dependency Inventory

### 0.6.1 Key Private and Public Packages

The following table lists all key packages relevant to this refactoring, with exact versions sourced from the monolith's `.csproj` files and validated through web search for new additions.

**Existing Packages (preserved from monolith):**

| Registry | Package Name | Version | Purpose |
|----------|-------------|---------|---------|
| NuGet | Npgsql | 9.0.4 | PostgreSQL ADO.NET driver |
| NuGet | AutoMapper | 14.0.0 | Object mapping / DTO transformations |
| NuGet | Newtonsoft.Json | 13.0.4 | JSON serialization (all API contracts) |
| NuGet | CsvHelper | 33.1.0 | CSV import/export |
| NuGet | Ical.Net | 5.1.4 | Recurrence scheduling |
| NuGet | Irony.NetCore | 1.1.11 | EQL grammar parser |
| NuGet | Storage.Net | 9.3.0 | Cloud blob storage abstraction |
| NuGet | System.Drawing.Common | 10.0.1 | Image dimension extraction |
| NuGet | MimeMapping | 3.1.0 | MIME type inference |
| NuGet | MailKit | 4.14.1 | SMTP email sending (Mail service) |
| NuGet | HtmlAgilityPack | (current) | HTML parsing in render service |
| NuGet | CSScriptLib | (current) | Dynamic C# code evaluation |
| NuGet | Microsoft.AspNetCore.Mvc.NewtonsoftJson | 10.0.1 | ASP.NET Core Newtonsoft integration |
| NuGet | Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.1 | JWT Bearer authentication |
| NuGet | Wangkanai.Detection | (current) | Client device detection |
| NuGet | System.IdentityModel.Tokens.Jwt | (current) | JWT token handling |
| NuGet | Microsoft.CodeAnalysis.* | (current) | Roslyn workspaces/scripting |
| NuGet | WebVella.TagHelpers | (current) | Custom Razor TagHelper library |
| Framework | Microsoft.AspNetCore.App | 10.0 | ASP.NET Core shared framework |

**New Packages (required for microservice architecture):**

| Registry | Package Name | Version | Purpose |
|----------|-------------|---------|---------|
| NuGet | Grpc.AspNetCore | 2.71.0 | gRPC server hosting for inter-service communication |
| NuGet | Google.Protobuf | 3.29.3 | Protocol Buffers serialization |
| NuGet | Grpc.Net.Client | 2.71.0 | gRPC client for service-to-service calls |
| NuGet | Grpc.Tools | 2.71.0 | Protobuf/gRPC code generation |
| NuGet | MassTransit | 8.4.0 | Message bus abstraction (RabbitMQ + Amazon SQS/SNS) |
| NuGet | MassTransit.RabbitMQ | 8.4.0 | RabbitMQ transport for local/Docker messaging |
| NuGet | MassTransit.AmazonSQS | 8.4.0 | Amazon SQS/SNS transport for LocalStack validation |
| NuGet | AWSSDK.SQS | 3.7.400 | AWS SQS client for LocalStack integration |
| NuGet | AWSSDK.SimpleNotificationService | 3.7.400 | AWS SNS client for LocalStack event topics |
| NuGet | AWSSDK.S3 | 3.7.400 | AWS S3 client for file storage via LocalStack |
| NuGet | Microsoft.Extensions.Caching.StackExchangeRedis | 10.0.1 | Distributed Redis caching |
| NuGet | StackExchange.Redis | 2.8.16 | Redis client library |
| NuGet | Testcontainers | 4.10.0 | Docker container management for tests |
| NuGet | Testcontainers.LocalStack | 4.10.0 | LocalStack module for integration tests |
| NuGet | Testcontainers.PostgreSql | 4.10.0 | PostgreSQL container for test isolation |
| NuGet | xunit | 2.9.3 | Test framework |
| NuGet | xunit.runner.visualstudio | 2.9.3 | VS/CLI test runner |
| NuGet | Microsoft.NET.Test.Sdk | 17.12.0 | Test platform SDK |
| NuGet | Moq | 4.20.72 | Mocking library for unit tests |
| NuGet | FluentAssertions | 7.2.0 | Assertion library for readable tests |
| NuGet | Microsoft.AspNetCore.Mvc.Testing | 10.0.1 | WebApplicationFactory for integration tests |
| NuGet | Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.2 | EF Core PostgreSQL provider for migrations |
| NuGet | Microsoft.EntityFrameworkCore.Design | 10.0.1 | EF Core migration tooling |
| Docker | localstack/localstack | latest | AWS service emulator container |
| Docker | postgres:16-alpine | 16 | PostgreSQL container per service |
| Docker | redis:7-alpine | 7 | Distributed caching |
| Docker | rabbitmq:3-management-alpine | 3 | Message broker for local development |

### 0.6.2 Import Refactoring

**Files requiring import updates (wildcard patterns):**

- `src/SharedKernel/**/*.cs` — Update all namespaces from `WebVella.Erp.*` to `WebVella.Erp.SharedKernel.*`
- `src/Services/WebVella.Erp.Service.Core/**/*.cs` — Update internal imports to `WebVella.Erp.Service.Core.*`
- `src/Services/WebVella.Erp.Service.Crm/**/*.cs` — Update to `WebVella.Erp.Service.Crm.*`
- `src/Services/WebVella.Erp.Service.Project/**/*.cs` — Update to `WebVella.Erp.Service.Project.*`
- `src/Services/WebVella.Erp.Service.Mail/**/*.cs` — Update to `WebVella.Erp.Service.Mail.*`
- `src/Services/WebVella.Erp.Service.Reporting/**/*.cs` — Update to `WebVella.Erp.Service.Reporting.*`
- `src/Services/WebVella.Erp.Service.Admin/**/*.cs` — Update to `WebVella.Erp.Service.Admin.*`
- `src/Gateway/WebVella.Erp.Gateway/**/*.cs` — Update to `WebVella.Erp.Gateway.*`
- `tests/**/*.cs` — Update all test imports to reference new service namespaces

**Import Transformation Rules:**

- Old: `using WebVella.Erp.Api;` → New: `using WebVella.Erp.SharedKernel.Models;` + `using WebVella.Erp.Service.Core.Api;` (where applicable)
- Old: `using WebVella.Erp.Hooks;` → New: `using WebVella.Erp.SharedKernel.Contracts.Events;`
- Old: `using WebVella.Erp.Jobs;` → New: Per-service `using WebVella.Erp.Service.{Name}.Jobs;`
- Old: `using WebVella.Erp.Notifications;` → New: MassTransit event integration
- Apply to: All `.cs` files matching `src/**/*.cs` and `tests/**/*.cs`

### 0.6.3 External Reference Updates

**Configuration files:**
- `docker-compose.yml`, `docker-compose.localstack.yml`, `docker-compose.override.yml` — New container orchestration
- Per-service `appsettings.json` — Replace monolith `Config.json` format

**Build files:**
- `Directory.Build.props` — Centralized MSBuild properties (TargetFramework=net10.0)
- `Directory.Packages.props` — Central Package Management for version pinning
- `WebVella.ERP3.sln` — Updated solution with new project structure
- `global.json` — Pin .NET SDK version to 10.0.x

**CI/CD:**
- `.github/workflows/ci.yml` — Multi-service build + test pipeline
- `.github/workflows/cd.yml` — Docker image build + push
- `.github/workflows/localstack-validation.yml` — E2E validation with LocalStack


## 0.7 Special Analysis

### 0.7.1 Cross-Cutting Entity Dependency Analysis

The monolith's dynamic entity system creates deep coupling that must be carefully resolved during decomposition. The following analysis maps entity ownership across service boundaries.

**Entity-to-Service Ownership Matrix:**

| Entity Name | Current Location | Target Service Owner | Cross-Service References |
|-------------|-----------------|---------------------|--------------------------|
| `user` | Core (ERPService.cs system entity) | Core | All services (created_by/modified_by audit fields) |
| `role` | Core (ERPService.cs system entity) | Core | All services (RecordPermissions) |
| `user_file` | Core (ERPService.cs system entity) | Core | Project (attachments), Mail (attachments) |
| `account` | Next plugin (NextPlugin.20190204.cs) | CRM | Project (account-project relation) |
| `contact` | Next plugin (NextPlugin.20190204.cs) | CRM | Mail (recipients) |
| `case` | Next plugin (NextPlugin.20190203.cs) | CRM | Project (case-task relation) |
| `address` | Next plugin (NextPlugin.20190204.cs) | CRM | — |
| `salutation` | Next plugin (NextPlugin.20190206.cs) | CRM | — |
| `language` | Next plugin (NextPlugin.20190204.cs) | Core | CRM (localization) |
| `currency` | Next plugin (NextPlugin.20190204.cs) | Core | CRM (account currency) |
| `task` | Next plugin (NextPlugin.20190203.cs) | Project | CRM (case-task links) |
| `timelog` | Next plugin (NextPlugin.20190203.cs) | Project | Reporting (aggregation) |
| `comment` | Plugin Project (services) | Project | — |
| `email` | Mail plugin (MailPlugin.20190215.cs) | Mail | — |
| `smtp_service` | Mail plugin (MailPlugin.20190215.cs) | Mail | — |
| `task_type` | Next plugin (NextPlugin.20190222.cs) | Project | — |
| `country` | Next plugin | Core | CRM (address country) |

**Cross-Service Relation Resolution Strategy:**

Relations that span service boundaries (e.g., `user_{entity}_created_by` or `account→project`) cannot use direct foreign keys in a database-per-service model. The following strategies apply:

| Relation Pattern | Current Implementation | Target Resolution |
|-----------------|----------------------|-------------------|
| Audit fields (`created_by`, `modified_by`) | FK to `rec_user` | Store user UUID; resolve via Core gRPC call on read |
| Account → Project | `rel_*` join table | Denormalized `account_id` in Project DB; eventual consistency via CRM events |
| Case → Task | `rel_*` join table | Denormalized `case_id` in Project DB; CRM publishes CaseUpdated events |
| Contact → Email | Implicit via sender/recipients JSON | Mail service stores contact UUID; resolves via CRM gRPC on read |
| User → Role | `rel_user_role` join table | Core service owns; JWT claims propagate role information |

### 0.7.2 Stateful Singleton Decomposition

The monolith relies on three critical stateful singletons that violate microservice statelessness principles:

**`JobManager.Current` (WebVella.Erp/Jobs/JobManager.cs):**
- Current: Singleton holding `JobManagerSettings`, job type registry, `JobDataService` persistence
- Current behavior: On startup, scans all assemblies for `[Job]`-attributed classes, marks interrupted Running jobs as Aborted
- Target: Each service hosts its own `IHostedService`-based job processor with a service-scoped job table. The bounded 20-thread pool is replaced by per-service configurable concurrency limits. Crash recovery is preserved per service.

**`ScheduleManager.Current` (WebVella.Erp/Jobs/SheduleManager.cs):**
- Current: Singleton managing schedule plans with PostgreSQL-backed persistence, 1-second polling loop
- Target: Each service manages its own schedule plans in its database. Interval/daily triggers remain, but each service independently computes next trigger times.

**`ErpAppContext.Current` (WebVella.Erp.Web/ErpAppContext.cs):**
- Current: Singleton holding root `IServiceProvider`, `WebSettings`, theme CSS, shared scripts
- Target: Eliminated. Each service uses ASP.NET Core's built-in DI. The Gateway/BFF handles theme CSS and script serving.

### 0.7.3 EQL Engine Cross-Service Query Resolution

The EQL engine currently supports cross-entity relation traversal (`$relation.field`, `$$relation.field`) that generates correlated subqueries joining across `rec_*` and `rel_*` tables. In a database-per-service model, these cross-service joins are impossible at the SQL level.

**Resolution approach:**
- **Intra-service relations** (e.g., account → contact within CRM): Preserved as-is with EQL generating local SQL joins
- **Cross-service relations** (e.g., account → task spanning CRM → Project): EQL builder detects cross-service field references and replaces correlated subqueries with API composition — the owning service executes EQL for local fields, then the Gateway composes results via gRPC calls to the related service
- **Search indexing** (x_search fields with `$relation.field` tokens): Each service indexes its own entities; cross-service fields are denormalized via event subscribers that update local projections

### 0.7.4 LocalStack Deployment Validation Architecture

The user requires deployment validation using LocalStack in Docker containers. The following architecture enables end-to-end testing:

**LocalStack Services Used:**
- **SQS** — Message queues for inter-service async communication
- **SNS** — Event topics for publish/subscribe patterns
- **S3** — File storage backend (replacing Storage.Net cloud blobs)

**Docker Compose Stack for Validation:**

```
docker-compose.localstack.yml:
  services:
    localstack:
      image: localstack/localstack:latest
      ports: ["4566:4566"]
      environment:
        - SERVICES=sqs,sns,s3
        - DEBUG=1
    
    postgres-core:
      image: postgres:16-alpine
      environment: { POSTGRES_DB: erp_core }
    
    postgres-crm:
      image: postgres:16-alpine
      environment: { POSTGRES_DB: erp_crm }
    
    postgres-project:
      image: postgres:16-alpine
      environment: { POSTGRES_DB: erp_project }
    
    postgres-mail:
      image: postgres:16-alpine
      environment: { POSTGRES_DB: erp_mail }
    
    redis:
      image: redis:7-alpine
    
    rabbitmq:
      image: rabbitmq:3-management-alpine
    
    core-service:
      build: src/Services/WebVella.Erp.Service.Core
      depends_on: [postgres-core, redis, localstack]
    
    crm-service:
      build: src/Services/WebVella.Erp.Service.Crm
      depends_on: [postgres-crm, redis, localstack]
    
    project-service:
      build: src/Services/WebVella.Erp.Service.Project
      depends_on: [postgres-project, redis, localstack]
    
    mail-service:
      build: src/Services/WebVella.Erp.Service.Mail
      depends_on: [postgres-mail, redis, localstack]
    
    gateway:
      build: src/Gateway/WebVella.Erp.Gateway
      ports: ["5000:8080"]
      depends_on: [core-service, crm-service, project-service, mail-service]
```

**Integration Test Approach:**
- Tests use `Testcontainers.LocalStack` (v4.10.0) to programmatically spin up LocalStack containers
- Each test class provisions the required SNS topics and SQS queues
- Cross-service integration tests validate that domain events published by one service are received and processed by subscribers in another service
- Schema migration tests verify zero data loss by comparing record counts and checksums before and after migration

### 0.7.5 Plugin Patch System Migration

The monolith uses a custom date-based versioning system for database migrations, stored as JSON in the `plugin_data` table. Each plugin (SDK, Next, Project, Mail, CRM, MicrosoftCDM) has a `ProcessPatches()` method that executes sequential patch methods (e.g., `Patch20190203`, `Patch20190419`) within a single database transaction.

**Migration to EF Core Migrations:**

| Current Pattern | Target Pattern |
|----------------|---------------|
| `PluginSettings.Version` (integer date, e.g., `20190203`) | EF Core `__EFMigrationsHistory` table |
| `GetPluginData()` / `SavePluginData()` | EF Core `DbContext.Database.Migrate()` |
| Partial class `*Plugin.YYYYMMDD.cs` patch methods | EF Core migration files per service |
| Manual `EntityManager.CreateEntity()` calls | EF Core `modelBuilder` configuration |
| Single transaction wrapping all patches | Per-migration transaction (EF Core default) |

Each service's initial EF Core migration will codify the current state of all entities owned by that service, including all fields, relations, indexes, and seed data extracted from the cumulative patch history.


## 0.8 Refactoring Rules

### 0.8.1 User-Specified Rules and Constraints

The following rules are explicitly mandated by the user and must be enforced without exception:

**Business Rule Preservation (Zero Tolerance):**
- All business rules must be preserved exactly as they exist in the monolith
- A cross-service integration test suite must validate all business rules extracted from the monolith
- Each business rule must map to at least one automated test that produces identical output to the monolith behavior on equivalent input
- Zero business rules may be marked as "preserved" without a corresponding passing test

**Data Integrity:**
- Zero data loss during schema migration — every record in every `rec_*` table must be accounted for in the target service's database
- Schema migration scripts must be idempotent and reversible
- Data migration must preserve all audit fields (`created_on`, `created_by`, `last_modified_on`, `last_modified_by`)

**Independent Deployability:**
- Each service must be independently deployable via Docker containers
- Services must be orchestratable via Kubernetes
- No service may require another service's database for its startup or core operation (eventual consistency is acceptable for cross-service data)

**API Contract Backward Compatibility:**
- All existing REST API v3 endpoints (`/api/v3/{locale}/...`) must remain accessible through the API Gateway
- Response shapes (BaseResponseModel envelope: `success`, `errors`, `timestamp`, `message`, `object`) must not change
- JWT authentication must remain compatible with existing tokens

### 0.8.2 Refactoring-Specific Technical Rules

**Architectural Constraints:**
- Services communicate only through well-defined API contracts (REST/gRPC) and asynchronous events (message broker) — no direct database access across service boundaries
- The Shared Kernel library contains only pure contracts (DTOs, events, interfaces) and stateless utilities — no service logic or database access
- Each service owns its database schema exclusively; no other service may read from or write to another service's database
- Event consumers must be idempotent (duplicate event delivery must not cause data corruption)

**Code Quality Standards:**
- Preserve the existing `.editorconfig` formatting rules across all services
- Follow the `PascalCase` naming convention for constants (as enforced by existing Roslyn rules)
- Maintain Newtonsoft.Json `[JsonProperty]` annotations for API contract stability
- Use `[Authorize]` attribute on all controllers by default (matching existing `ApiControllerBase` behavior)

**Testing Requirements:**
- Unit tests: All business logic classes must have ≥80% code coverage
- Integration tests: Every REST endpoint must have at least one happy-path and one error-path test
- Cross-service tests: Every business rule that spans two or more services must have an integration test using Testcontainers
- LocalStack validation: End-to-end tests must validate message flow through SNS/SQS and file operations through S3

### 0.8.3 Special Instructions and Constraints

**Deployment validation via LocalStack:**
- All cloud-native features (message queues, event topics, file storage) must be validated against LocalStack running in a Docker container
- The `docker-compose.localstack.yml` file must bring up a fully functional stack that passes all integration tests without any external cloud dependencies
- LocalStack endpoint configuration must be injectable via environment variables to support switching between local and production AWS endpoints

**Security Context Propagation:**
- The current `SecurityContext` using `AsyncLocal<Stack<ErpUser>>` must be adapted for cross-service token propagation
- JWT tokens issued by the Core service must contain all necessary claims (user ID, roles, permissions) for downstream services to authorize requests without callback to the Core service
- The `OpenSystemScope()` pattern must be preserved within each service for background job execution

**EQL Engine Preservation:**
- The EQL grammar, AST, builder, and SQL generator must remain functionally identical to the monolith
- Intra-service EQL queries must produce identical SQL and results
- Cross-service EQL queries must degrade gracefully to API composition without error

**Performance Baselines:**
- EQL query timeout (600 seconds) must be preserved per service
- Database connection pooling (min 1, max 100) must be configurable per service
- Entity metadata cache TTL (1 hour) must be preserved per service


## 0.9 References

### 0.9.1 Codebase Files and Folders Searched

The following files and folders were systematically explored to derive all conclusions in this Agent Action Plan:

**Root-Level Files:**
- `.gitattributes` — Git attributes template
- `LIBRARIES.md` — Third-party library placeholder
- `LICENSE.txt` — Apache License 2.0
- `README.md` — Project landing page (confirms ASP.NET Core 9+ PostgreSQL 16; projects target `net10.0`)
- `WebVella.ERP3.sln` — Visual Studio solution (19 projects)
- `create-nuget-pkgs.bat` — NuGet packaging automation
- `global.json` — .NET SDK selection (version commented out)

**Core Engine (`WebVella.Erp/`):**
- `WebVella.Erp.csproj` — Build definition (net10.0, v1.7.7, all NuGet dependencies with exact versions)
- `ERPService.cs` — Bootstrap orchestrator, system entity initialization
- `ErpPlugin.cs` — Abstract plugin base class
- `ErpSettings.cs` — Global configuration binder
- `IErpService.cs` — Service contract interface
- `Api/` — Complete folder (Cache.cs, DataSourceManager.cs, Definitions.cs, EntityManager.cs, EntityRelationManager.cs, ImportExportManager.cs, RecordManager.cs, SearchManager.cs, SecurityContext.cs, SecurityManager.cs)
- `Api/Models/` — Complete folder (35+ DTO/contract files)
- `Database/` — Complete folder (DbContext.cs, DbConnection.cs, DbRepository.cs, DbEntityRepository.cs, DbRecordRepository.cs, DbRelationRepository.cs, DbFileRepository.cs, DbDataSourceRepository.cs, DBTypeConverter.cs, DbSystemSettings.cs, DbSystemSettingsRepository.cs)
- `Eql/` — Complete folder (13 files: grammar, AST, builder, SQL, command, parameters, errors, settings)
- `Hooks/` — Complete folder (20 files: attributes, managers, 12 hook interfaces)
- `Jobs/` — Complete folder (7 files: hosted services, pool, managers + Models/)
- `Notifications/` — Complete folder (5 files: LISTEN/NOTIFY pub/sub)

**Web Layer (`WebVella.Erp.Web/`):**
- `WebVella.Erp.Web.csproj` — Build definition (net10.0, Razor SDK)
- `ErpMvcExtensions.cs` — AddErp/UseErp startup extensions
- `ErpAppContext.cs` — Singleton web context
- `ErpRequestContext.cs` — Scoped request routing
- `Controllers/` — ApiControllerBase.cs, WebApiController.cs
- `Services/` — Complete folder (18 service files)
- `Middleware/` — Complete folder (6 middleware files)
- `Models/`, `Repositories/`, `Pages/`, `Components/`, `TagHelpers/`, `Hooks/`, `Security/`

**Plugin Projects:**
- `WebVella.Erp.Plugins.SDK/` — Complete folder (SdkPlugin.cs, _.cs, 5 patches, Controllers/, Services/, Jobs/, Pages/, Components/, wwwroot/)
- `WebVella.Erp.Plugins.Next/` — Complete folder (NextPlugin.cs, _.cs, 5 patches, Configuration.cs, Hooks/Api/, Services/, Model/)
- `WebVella.Erp.Plugins.Project/` — Complete folder (ProjectPlugin.cs, _.cs, 9 patches, Controllers/, Services/, Components/, Hooks/, Jobs/, Files/, Theme/, Utils/, Model/, wwwroot/)
- `WebVella.Erp.Plugins.Mail/` — Complete folder (MailPlugin.cs, _.cs, 7 patches, Api/, Services/, Hooks/, Jobs/)
- `WebVella.Erp.Plugins.Crm/` — Complete folder (CrmPlugin.cs, _.cs, Model/)
- `WebVella.Erp.Plugins.MicrosoftCDM/` — Complete folder (MicrosoftCDMPlugin.cs, _.cs, Model/, wwwroot/)

**Site Host Projects:**
- `WebVella.Erp.Site/` — Config.json, Program.cs, Startup.cs, csproj, web.config, JWT_README.txt
- `WebVella.Erp.Site.Project/` — Config.json, Program.cs, Startup.cs, csproj
- `WebVella.Erp.Site.Sdk/`, `.Next/`, `.Mail/`, `.Crm/`, `.MicrosoftCDM/` — All examined at folder level

**Other Projects:**
- `WebVella.Erp.ConsoleApp/` — Program.cs, RoleRecordHooks.cs, UserRecordHooks.cs, csproj
- `WebVella.Erp.WebAssembly/` — Client/, Server/, Shared/ (all examined at folder level)
- `.github/FUNDING.yml` — GitHub Sponsors config
- `docs/` — Developer documentation tree

### 0.9.2 Technical Specification Sections Referenced

- **1.1 Executive Summary** — Project overview, version (1.7.7), stakeholders, value proposition
- **5.1 HIGH-LEVEL ARCHITECTURE** — System boundaries, component table, data flows, integration patterns, technology foundation
- **6.1 Core Services Architecture** — Modular monolith classification, component topology, communication patterns, scalability constraints, resilience patterns
- **6.2 Database Design** — Hybrid relational-document model, schema design, field type mappings, indexing strategy, migration procedures, caching policies, connection pooling

### 0.9.3 External Research Conducted

- **.NET 10 release status:** Confirmed .NET 10 as the latest stable LTS release (November 2025, supported until November 2028), current patch version 10.0.1 (December 2025). The repository already targets `net10.0`.
- **LocalStack for Docker-based validation:** LocalStack CLI v4.9.0, Docker image `localstack/localstack:latest`, supports SQS/SNS/S3 emulation on port 4566. `Testcontainers.LocalStack` NuGet package v4.10.0 provides programmatic container management for .NET integration tests.
- **Microservice testing with Docker Compose:** Standard pattern uses `docker-compose.yml` to orchestrate multiple service containers + infrastructure (PostgreSQL, Redis, RabbitMQ, LocalStack) for end-to-end testing.

### 0.9.4 Attachments

No attachments were provided for this project. No Figma URLs were specified.


