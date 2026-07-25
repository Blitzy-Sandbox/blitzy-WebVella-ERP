# WebVella.Erp.Worker

`WebVella.Erp.Worker` is the container-native background **worker** host for the headless WebVella ERP platform. It runs scheduled and queued **jobs** outside the HTTP request path, on the unchanged `WebVella.Erp` core engine and its shared PostgreSQL database, so that recurring work (outbound email delivery, daily task rollovers, and other recurrence/scheduled jobs) never competes with API request traffic.

> **Documentation-only, target-state description.** The worker project's implementation (its `.csproj`, `Program.cs`, hosted services, and scheduler wiring) is delivered by a separate implementation workstream and does **not** exist in this checkout yet. This README describes the worker's **target design** in forward-looking tense; it does not claim the code is already present. `Source: /WebVella.Erp/WebVella.Erp.csproj:L4` (the worker builds on the `net10.0` core engine).

## What it does

The worker is the dedicated host for the platform's recurring and queued background **jobs**. Each job is a `WebVella.Erp` core `ErpJob` scheduled through a **schedule plan** managed by the core `ScheduleManager`: the platform checks every minute for jobs that need to start and runs each on its own background thread. See [Background jobs — Overview](../docs/developer/background-jobs/overview.md) for the job and schedule-plan concepts, and [Observability](../docs/architecture/observability.md) for how job runs are logged and traced.

The refactor relocates two concrete jobs — today defined in-process by the Mail and Project plugins — into this worker host:

- **SMTP-queue processing** — drains the outbound email queue on a recurring interval of roughly every 10 minutes, running `SmtpInternalService.ProcessSmtpQueue()` to send queued messages over SMTP (MailKit). `Source: /WebVella.Erp.Plugins.Mail/Jobs/ProcessSmtpQueueJob.cs` (job class `ProcessSmtpQueueJob : ErpJob`); `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L829` (`ProcessSmtpQueue()`); `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L56` (`SchedulePlanType.Interval`), `:L69` (`IntervalInMinutes = 10`), `:L57` (00:10 UTC start), `:L41` (`SetSchedulePlans()`); `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj` (MailKit 4.14.1).
- **Daily project "start tasks" job** — starts project tasks whose start date has arrived, once per day at 00:10 UTC, by calling `TaskService().GetTasksThatNeedStarting()` and updating each task's status via `RecordManager().UpdateRecord`. `Source: /WebVella.Erp.Plugins.Project/Jobs/StartTasksOnStartDate.cs` (job class `StartTasksOnStartDate : ErpJob`); `Source: /WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L46` (`SchedulePlanType.Daily`), `:L47` (00:10 UTC start), `:L59` (`IntervalInMinutes = 1440`), `:L31` (`SetSchedulePlans()`).

Beyond these two, the worker hosts **other `WebVella.Erp` recurrence and scheduled jobs** registered through the same core infrastructure — the `[Job]` attribute, `ErpJob`, `JobManager`, `ScheduleManager`, `SchedulePlan`, and `ErpBackgroundServices` — and uses the same core engine and Npgsql data access as the API tier. `Source: /WebVella.Erp/WebVella.Erp.csproj:L4` (`net10.0`), `:L11` (Version 1.7.7), `:L61` (Npgsql 9.0.4).

> **Legacy contrast (one line).** In the legacy monolith these jobs ran in-process inside the site host via each plugin's `SetSchedulePlans()`; the refactor relocates recurring/queued work into this dedicated `WebVella.Erp.Worker` host that runs outside the API request path. `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L41`; `Source: /WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L31`.

## How to run, build, and test

### Run locally

```bash
dotnet run --project WebVella.Erp.Worker
```

### Build

The worker builds as part of the solution:

```bash
dotnet build WebVella.ERP3.sln
```

### Run as a container

In the container-native model the worker ships as a container image and runs as the **`worker`** service alongside the API, a one-shot migrator, the database, and the identity provider. See [Docker Compose](../docs/deployment/docker-compose.md) for the quick-start topology.

### Prerequisites

- A reachable **PostgreSQL** database, shared with the API and core engine via Npgsql. `Source: /WebVella.Erp/WebVella.Erp.csproj:L61`.
- **SMTP settings** for the SMTP-queue job (see [Key configuration and defaults](#key-configuration-and-defaults)).

### Decision points

Per rule F, unresolved choices are surfaced explicitly rather than silently assumed:

| Item | Status |
|------|--------|
| Worker scheduler (Quartz.NET vs. Hangfire) | **Not available / to be confirmed.** The worker will host recurring jobs regardless of the eventual scheduler choice. |
| Target runtime (.NET 9 vs .NET 10 / `net10.0`) | **Not available / to be confirmed.** The core engine targets `net10.0` (`Source: /WebVella.Erp/WebVella.Erp.csproj:L4`) while the specification says ".NET 9", and `global.json` has its SDK pin commented out. |
| Test project | **Not available** — there is no test project in the repository today. |

## Key configuration and defaults

Configuration is supplied to the worker as **environment variables** (and, in Kubernetes, as **Secrets**) by **key name only**. **No secret values** — connection strings, passwords, SMTP credentials, JWT signing keys, or OTLP tokens — appear in this document or in source control; secrets are provided at deploy time via environment variables / Kubernetes Secrets. Keys use the ASP.NET Core environment-variable form, where the configuration `:` separator maps to `__` (for example, `Settings:ConnectionString` becomes `Settings__ConnectionString`).

| Setting (env var) | Purpose | Default |
|-------------------|---------|---------|
| `Settings__ConnectionString` | PostgreSQL connection string used by Npgsql for all data access. `Source: /WebVella.Erp/ErpSettings.cs:L10`; `Source: /WebVella.Erp/WebVella.Erp.csproj:L61`. | `— (required; provide via Secret)` |
| Worker scheduler settings | Scheduler-specific keys (e.g. concurrency, misfire handling) for the recurring-job scheduler. | `Not available / to be confirmed` (Quartz.NET vs. Hangfire) |
| `Settings__EmailEnabled` | Enables the SMTP-queue job; when `false` the mail job does not send. `Source: /WebVella.Erp/ErpSettings.cs:L29`. | `false` |
| `Settings__EmailSMTPServerName` | SMTP host for the mail job. `Source: /WebVella.Erp/ErpSettings.cs:L30`. | `— (required when email enabled)` |
| `Settings__EmailSMTPPort` | SMTP port for the mail job. `Source: /WebVella.Erp/ErpSettings.cs:L31`. | `— (required when email enabled)` |
| `Settings__EmailSMTPUsername` | SMTP username — **secret**. `Source: /WebVella.Erp/ErpSettings.cs:L32`. | `— (required; provide via Secret)` |
| `Settings__EmailSMTPPassword` | SMTP password — **secret**. `Source: /WebVella.Erp/ErpSettings.cs:L33`. | `— (required; provide via Secret)` |
| `Serilog__MinimumLevel__Default` | Minimum level for structured JSON logging. See [Observability](../docs/architecture/observability.md). | `Information` (proposed; to be confirmed) |
| OTLP exporter endpoint (e.g. `OTEL_EXPORTER_OTLP_ENDPOINT`) | OTLP collector endpoint for trace/metric export — **may be a secret** if it embeds credentials. See [Observability](../docs/architecture/observability.md). | `— (provide via env/Secret)` |

The consolidated, authoritative key list is the [Configuration Reference](../docs/deployment/configuration-reference.md).

## Common failure modes and troubleshooting

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| A job never fires | Scheduler misconfiguration, or timezone confusion — all schedule plans are expressed in **UTC** (for example, the daily task starter fires at **00:10 UTC**). `Source: /WebVella.Erp.Plugins.Project/ProjectPlugin.cs:L47`. | Verify the scheduler configuration (choice **to be confirmed**) and that each job's schedule plan is present and `Enabled`; interpret all schedule times as UTC. |
| Outbound email is not sent / stuck in the queue | `Settings__EmailEnabled=false`, an unreachable SMTP host/port, or invalid SMTP credentials. | Confirm `Settings__EmailEnabled=true` and that the `Settings__EmailSMTP*` keys are set (values via Secret). The queue is retried on the next interval (~10 min). `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L69`. |
| Worker cannot start / Npgsql connection errors | The worker cannot reach PostgreSQL, `Settings__ConnectionString` is missing or incorrect, or the schema has not been created. | Verify `Settings__ConnectionString` (provided via Secret), that the database is reachable, and that the one-shot migrator has run. `Source: /WebVella.Erp/WebVella.Erp.csproj:L61`. |
| A job run cannot be traced end to end | The correlation ID is not propagated across SPA → API → worker. | Ensure enqueued work carries the correlation ID so a single operation can be traced across tiers; see the correlation-ID guidance in [Observability](../docs/architecture/observability.md). |

For the full operator playbook, see [Troubleshooting](../docs/deployment/troubleshooting.md).

## See also

- [Background jobs — Overview](../docs/developer/background-jobs/overview.md) — job and schedule-plan concepts.
- [Observability](../docs/architecture/observability.md) — structured logging, correlation IDs, and OTLP export.
- [Docker Compose](../docs/deployment/docker-compose.md) — running the `worker` service locally.
- [Configuration Reference](../docs/deployment/configuration-reference.md) — the authoritative env-var / Secret key list.
- [Troubleshooting](../docs/deployment/troubleshooting.md) — operational failure modes and remedies.
