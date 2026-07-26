# WebVella.Erp.Plugins.Project

The **Project** plugin provides project management for WebVella ERP — projects, tasks, time logging/timesheets, comments/feed, task watchers, and billable/non-billable reporting. It is implemented as `public partial class ProjectPlugin : ErpPlugin` with the plugin name `"project"`. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L10-L13`

## What it does

The Project plugin is the ERP's project/task/time-management module. Its public capabilities are:

- **Projects & tasks** — project and task records, task queues, statuses, and watchers. `Source: WebVella.Erp.Plugins.Project/Services/TaskService.cs`
- **Time logging & timesheets** — timelog create/read/update/query backing timesheet views. `Source: WebVella.Erp.Plugins.Project/Services/TimeLogService.cs`
- **Billable / non-billable reporting** — monthly reporting aggregates **billable vs non-billable** minutes per task/project via `ReportService.GetTimelogData(year, month, accountId)`. `Source: WebVella.Erp.Plugins.Project/Services/ReportService.cs`
- **Comments / feed** — post and comment feed for project collaboration. `Source: WebVella.Erp.Plugins.Project/Services/CommentService.cs`
- **Daily task starter (background job)** — see below.

On host startup the plugin's `Initialize(IServiceProvider)` opens a system security scope and runs `ProcessPatches()` (versioned schema/data patches) followed by `SetSchedulePlans()` (registers the daily job). `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L15-L22`

**Richest bundled plugin.** The project is organized into the feature folders `Components/`, `Controllers/`, `Datasource/`, `Files/`, `Hooks/`, `Jobs/`, `Model/`, `Services/`, `Theme/`, `Utils/`, and `wwwroot/`, plus many dated/versioned patch partials spanning 2019→2025 (`ProjectPlugin.20190203.cs` … `ProjectPlugin.20251229.cs`) applied by the patch orchestrator. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin._.cs:L56-L252`

**Daily "start tasks" background job (00:10 UTC).** `SetSchedulePlans()` ensures a Daily `SchedulePlan` named `"Start tasks on start_date"` that runs at **00:10 UTC** every day (`IntervalInMinutes = 1440`, all seven days enabled). `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L31-L72` The job itself is `class StartTasksOnStartDate : ErpJob`, decorated `[Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", "Start tasks on start_date", true, JobPriority.Low)]`; its `Execute(JobContext)` finds tasks whose start date has arrived and updates their `status_id`. `Source: WebVella.Erp.Plugins.Project/Jobs/StartTasksOnStartDate.cs`

**HTTP surface (today).** The current implementation exposes an MVC controller, `ProjectController`, routed under the legacy prefix `/api/v3.0/p/project/`. The controller carries a **class-level `[Authorize]`**, so its actions require an authenticated user **except** the one action that opts out with **`[AllowAnonymous]`** — the `GET /api/v3.0/p/project/files/javascript` route, which is **anonymous** (see the endpoint table and the JavaScript-route note below). Per rule B, the public endpoints are documented below.

**Error contract (important).** The write actions do **not** use a uniform `Success=false` envelope. On the **success** path an action returns a JSON `ResponseModel` (a `Success` flag, a message, and an optional object) with `Success = true`; but **input-validation and processing errors `throw` exceptions that propagate** (surfacing as an HTTP 500 / framework error response) rather than returning `Success = false` with a message. For example, the create and delete actions `throw new Exception("relatedRecordId is required")` / `"... is invalid Guid"` / `"id is required"` / `"id is invalid Guid"`, and the service-call `try/catch` blocks rethrow. Do **not** rely on a uniform `Success=false` error envelope. All line references are in `WebVella.Erp.Plugins.Project/Controllers/ProjectController.cs`. `Source: :L18` (class-level `[Authorize]`), `:L462` (`[AllowAnonymous]` on `files/javascript`), `:L67,L75,L151,L159,L267,L275` (validation `throw`s), `:L113-L120,L163-L170,L280-L286` (service-call `try { … } catch (Exception) { throw; }`), `:L122,L171,L288` (`Success = true` on the success path).

| Method | Path | Purpose | Inputs | Output / errors | Lines |
|--------|------|---------|--------|-----------------|-------|
| POST | `/api/v3.0/p/project/pc-post-list/create` | Create a project post/comment feed item | `EntityRecord` (JSON body) | JSON `ResponseModel` (`Success=true`) on success; **validation errors `throw`** (`"relatedRecordId is required"` / `"... is invalid Guid"`) and propagate, not a `Success=false` envelope | L56-L122 |
| POST | `/api/v3.0/p/project/pc-post-list/delete` | Delete a post/comment feed item | `EntityRecord` (JSON body) | JSON `ResponseModel` | L142-L144 |
| POST | `/api/v3.0/p/project/pc-timelog-list/create` | Create a timelog entry | `EntityRecord` JSON body (`body`, `minutes`, `isBillable`, `relatedRecords`) | JSON `ResponseModel` | L177-L179 |
| POST | `/api/v3.0/p/project/pc-timelog-list/delete` | Delete a timelog entry | `EntityRecord` (JSON body) | JSON `ResponseModel` | L257-L259 |
| POST | `/api/v3.0/p/project/timelog/start` | Start a timelog for a task | `taskId` (query, `Guid`) | JSON `ResponseModel` | L295-L297 |
| POST | `/api/v3.0/p/project/task/status` | Set a task's status | `taskId`, `statusId` (query, `Guid`) | JSON `ResponseModel` | L362-L364 |
| POST | `/api/v3.0/p/project/task/watch` | Add/remove a task watcher | `taskId?`, `userId?` (query, `Guid?`), `startWatch` (query, `bool`, default `true`) | JSON `ResponseModel` | L396-L398 |
| GET | `/api/v3.0/p/project/files/javascript` | Serve the plugin's embedded component JavaScript | **`file`** (query, `string`, default `""`) — caller-controlled embedded-resource name | `text/javascript` content; **`[AllowAnonymous]`** (no auth); response **cached 30 days** via `[ResponseCache]` (`Duration = 30 * 24 * 3600`); a blank `file` returns empty content, and a read error is **logged and rethrown** (propagates, not an envelope) | L462-L480 |
| GET | `/api/v3.0/p/project/user/get-current` | Return the current authenticated user record | — | JSON user record | L486-L488 |

(The `timelog/stop` action is present but commented out. `Source: /WebVella.Erp.Plugins.Project/Controllers/ProjectController.cs:L328-L330`.) Under the headless refactor this MVC surface moves toward the `/api/v1/` + `IErpPlugin.MapEndpoints` model — see the cross-links below.

### Refactor note

The plugin's code is **unchanged** by the documentation workstream; the description above is today's state. Under the headless, container-native platform the plugin adopts the `IErpPlugin` contract, and its scheduled daily job is hosted by the new `WebVella.Erp.Worker`. The worker's scheduler (Quartz.NET vs. Hangfire) is **Not available / to be confirmed**. See:

- [`IErpPlugin` contract](../docs/plugin-sdk/ierplugin-contract.md)
- [Plugin migration](../docs/migration/plugin-migration.md)
- [Background jobs overview](../docs/developer/background-jobs/overview.md)

## How to run, build, and test

This is a **.NET class library** targeting `net10.0`, built with `Microsoft.NET.Sdk.Razor` (referencing `Microsoft.AspNetCore.App`) and depending on `Microsoft.AspNetCore.Mvc.NewtonsoftJson` `10.0.1`. `Source: WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj:L4,L52` The authoritative platform-wide target framework is **Not available / to be confirmed**: the refactor specification states ".NET 9" while the code targets `net10.0`. The missing authority is a project-wide target-framework decision — to be pinned in the refactor specification and the solution build configuration (`global.json`) — which must be reconciled before the platform target is stated as fact; until then this README documents the value the `.csproj` currently declares. `Source: WebVella.Erp/WebVella.Erp.csproj:L4` (`net10.0`).

It is **not run standalone**: it is host-loaded by the ERP application and built as part of the solution `WebVella.ERP3.sln`.

```bash
# Build the whole solution
dotnet build WebVella.ERP3.sln -c Release

# Or build just this plugin
dotnet build WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj -c Release
```

Project references are `WebVella.Erp.Web` and `WebVella.Erp` (the core engine). `Source: WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj:L56-L57`

**Testing: Not available.** There is no test project for this plugin in the repository, so no test command is documented here.

## Key configuration and defaults

Configuration is documented by **key name / concept only**; no secret values appear here. In the **current** in-process model, platform settings (database connection, credentials, tokens) bind from `Config.json` via `ErpSettings`; the **target** container-native model supplies the same settings as **environment variables / Kubernetes Secrets** (never committed). `Source: /WebVella.Erp/ErpSettings.cs`. See the consolidated [configuration reference](../docs/deployment/configuration-reference.md).

| Setting | Purpose | Default |
|---------|---------|---------|
| Daily task-starter schedule | Time the "Start tasks on start_date" plan runs | **00:10 UTC**, daily (all days); `IntervalInMinutes = 1440`. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L47,L59` |
| Timelog `is_billable` | Per-timelog billable flag aggregated into billable vs non-billable minutes in reporting | Concept only — no literal value. `Source: WebVella.Erp.Plugins.Project/Services/ReportService.cs` |
| Plugin version (`plugin_data`) | Installed plugin version, stored as JSON in the `plugin_data` entity; drives which patches run | Init constant `WEBVELLA_PROJECT_INIT_VERSION = 20190101`. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin._.cs:L13,L47-L52,L255` |

## Common failure modes and troubleshooting

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| Daily "start tasks" job does not fire | The schedule plan is disabled, or the host running the schedule loop is down | **Today** the job runs **in-process** via the core `ScheduleManager` / `JobManager` loop inside the ERP host (not a separate worker); verify that host is running and that the "Start tasks on start_date" plan exists and is enabled. **Target:** the job moves to the planned `WebVella.Erp.Worker` host (**not yet built** — AAP §0.9.2). `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L31-L72`; `Source: /WebVella.Erp/Jobs/SheduleManager.cs:L223`. |
| Timelog report gaps / wrong billable totals | Timelogs missing scope/related-record links, or missing the `is_billable` flag | Verify the timelog records (scope, related task, billable flag) and re-run reporting. `Source: WebVella.Erp.Plugins.Project/Services/ReportService.cs` |
| Patch / init-version failure on startup | A dated patch throws during initialization | The entire init transaction **rolls back** and the stored version does not advance; inspect logs, fix the offending data, and restart to re-run. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin._.cs:L255-L272` |
| Static-asset / theme not loading | Embedded component `service.js` / `Files/*.js` / `Theme/styles.css` not served | Confirm the assets are embedded and the plugin is loaded by the host. `Source: WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj:L30-L49` |

For platform-wide operational troubleshooting see [deployment troubleshooting](../docs/deployment/troubleshooting.md).

## See also

- [`IErpPlugin` contract](../docs/plugin-sdk/ierplugin-contract.md) — the new `OnLoadAsync` / `MapEndpoints` / `OnMigrateAsync` lifecycle.
- [Migrating from `ErpPlugin`](../docs/plugin-sdk/migrating-from-erpplugin.md) — step-by-step port from the legacy base class.
- [Plugin migration](../docs/migration/plugin-migration.md) — program-level plugin migration (covers this plugin's daily 00:10 UTC job and billable/non-billable timelog).
- [Background jobs overview](../docs/developer/background-jobs/overview.md) — background-jobs model and worker-host framing.
- [Configuration reference](../docs/deployment/configuration-reference.md) — env-var / Kubernetes-Secret reference (key names only).
- [Troubleshooting](../docs/deployment/troubleshooting.md) — operational troubleshooting.
