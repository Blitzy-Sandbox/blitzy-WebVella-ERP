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

**HTTP surface (today).** The current implementation exposes an MVC `[Authorize]` controller under `/api/v3.0/p/project/*` (comments, timelogs, task start/status/watch, embedded JS). `Source: WebVella.Erp.Plugins.Project/Controllers/ProjectController.cs` Under the headless refactor this moves toward the `/api/v1/` + `IErpPlugin.MapEndpoints` model — see the cross-links below.

### Refactor note

The plugin's code is **unchanged** by the documentation workstream; the description above is today's state. Under the headless, container-native platform the plugin adopts the `IErpPlugin` contract, and its scheduled daily job is hosted by the new `WebVella.Erp.Worker`. The worker's scheduler (Quartz.NET vs. Hangfire) is **Not available / to be confirmed**. See:

- [`IErpPlugin` contract](../docs/plugin-sdk/ierplugin-contract.md)
- [Plugin migration](../docs/migration/plugin-migration.md)
- [Background jobs overview](../docs/developer/background-jobs/overview.md)

## How to run, build, and test

This is a **.NET class library** targeting `net10.0`, built with `Microsoft.NET.Sdk.Razor` (referencing `Microsoft.AspNetCore.App`) and depending on `Microsoft.AspNetCore.Mvc.NewtonsoftJson` `10.0.1`. `Source: WebVella.Erp.Plugins.Project/WebVella.Erp.Plugins.Project.csproj:L4,L52` (The authoritative platform-wide target — ".NET 9" vs `net10.0` — is an open decision point and is **to be confirmed**.)

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

Configuration is documented by **key name / concept only**; no secret values appear here. Secrets (database connection, credentials, tokens) are supplied via environment variables / Kubernetes Secrets and are never committed — see the consolidated [configuration reference](../docs/deployment/configuration-reference.md).

| Setting | Purpose | Default |
|---------|---------|---------|
| Daily task-starter schedule | Time the "Start tasks on start_date" plan runs | **00:10 UTC**, daily (all days); `IntervalInMinutes = 1440`. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L47,L59` |
| Timelog `is_billable` | Per-timelog billable flag aggregated into billable vs non-billable minutes in reporting | Concept only — no literal value. `Source: WebVella.Erp.Plugins.Project/Services/ReportService.cs` |
| Plugin version (`plugin_data`) | Installed plugin version, stored as JSON in the `plugin_data` entity; drives which patches run | Init constant `WEBVELLA_PROJECT_INIT_VERSION = 20190101`. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin._.cs:L13,L47-L52,L255` |

## Common failure modes and troubleshooting

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| Daily "start tasks" job does not fire | Scheduler/worker host down, or the schedule plan is disabled | Verify the worker host (`WebVella.Erp.Worker`) is running and that the "Start tasks on start_date" plan exists and is enabled. `Source: WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L31-L72` |
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
