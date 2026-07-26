# WebVella.Erp.Plugins.Mail

Plugin for WebVella.Erp that handles sending emails. It adds email delivery and a persistent SMTP outbox/queue to a WebVella ERP host, including the ERP entities, background job, hooks and services needed to compose, queue, send and retry email through one or more SMTP servers.

> This file is also the NuGet package landing page for `WebVella.Erp.Plugins.Mail` (v1.7.5). `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj:L6,L13`

## What it does

The Mail plugin provides **email sending** and an **SMTP outbox/queue** for WebVella ERP. It is implemented today as `public partial class MailPlugin : ErpPlugin` with plugin `Name = "mail"`. `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L14,L17`

- **Email transport** uses **MailKit 4.14.1**. `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj:L28` — MailKit's `SmtpClient` is driven by the SMTP engine. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L2,L788`
- **ERP entities.** The install patch creates two entities. `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.20190215.cs:L14,L580`
  - **`email`** — the outbox/queue entity. Representative fields: `subject`, `content_html` / `content_text`, sender/recipient fields (`from_name` / `from_email`, `to_name` / `to_email`), `priority`, `status`, `scheduled_on`, `sent_on`, `server_error`, `retries_count` and `service_id`.
  - **`smtp_service`** — the SMTP server-configuration entity. Representative fields: `server`, `port`, `username`, `password`, `connection_security`, `is_enabled`, `is_default`, `max_retries_count`, `retry_wait_minutes` and default sender / reply-to fields.
- **Background SMTP-queue job.** `ProcessSmtpQueueJob : ErpJob` drains the queue by calling `new SmtpInternalService().ProcessSmtpQueue()`. `Source: /WebVella.Erp.Plugins.Mail/Jobs/ProcessSmtpQueueJob.cs:L7-L16` The plugin registers a schedule plan that runs the job **every ~10 minutes** (`IntervalInMinutes = 10`). `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L41-L82`
- **Project layout.** `Api/` (DTOs / enums + AutoMapper), `Hooks/` (SMTP record and page hooks), `Jobs/` (the queue job) and `Services/` — `SmtpInternalService` is the SMTP engine handling validation, send/test, MIME / attachments / inline resources and queue processing. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs`

**Target state (headless refactor).** Under the headless, container-native refactor the scheduled SMTP-queue job moves to the new **`WebVella.Erp.Worker`** host and the plugin adopts the **`IErpPlugin`** contract in place of the legacy `ErpPlugin` base. See the links under **Related documentation** below.

## How to build, run and test

- **Project type.** A Razor SDK class library — `Microsoft.NET.Sdk.Razor`, `<TargetFramework>net10.0</TargetFramework>`, `AddRazorSupportForMvc=true`. `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj:L1,L4,L5` (The target framework is `net10.0` in code while the specification references ".NET 9"; the authoritative target is to be confirmed.)
- **Build.** Build as part of the solution:

  ```bash
  dotnet build WebVella.ERP3.sln
  ```

  or build this project on its own:

  ```bash
  dotnet build WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj
  ```

  The project references `WebVella.Erp.Web` and `WebVella.Erp`. `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj:L32-L33`
- **Run.** The plugin is **not** a standalone executable; it is **loaded by a WebVella ERP host**. On load, `Initialize(IServiceProvider)` opens a system security scope and runs `ProcessPatches()` (schema / data migration) followed by `SetSchedulePlans()` (registers the SMTP-queue schedule). `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L19-L26`
- **Test.** A dedicated automated test project for this plugin is **Not available** in the repository. For a manual check, the admin UI exposes a "test service" action via the `TestSmtpService` page hook, which verifies connectivity for a configured SMTP service. `Source: /WebVella.Erp.Plugins.Mail/Hooks/Page/TestSmtpService.cs`

## Configuration

There are **two distinct email paths with two distinct enable switches** — do not conflate them:

- **SMTP outbox/queue (this plugin's background job).** Enabled and configured **per SMTP server** via `smtp_service` **records** (data-level); the record's `is_enabled` field is the queue's on/off switch. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L700-L702` (aborts "SMTP service is not enabled" when `is_enabled = false`). See the `smtp_service` note below.
- **Web-layer direct send (`MailService`).** A *separate* code path in `WebVella.Erp.Web`, gated by the host setting `Settings:EmailEnabled` and using the `Settings:EmailSMTP*` values. `Source: /WebVella.Erp.Web/Services/MailService.cs:L24` (`if (ErpSettings.EmailEnabled)`), `:L35` (reads `EmailSMTP*`).

The `Settings:*` keys in the table below configure the **direct-send path**, not the queue. In the **current** model they bind from the `Settings` section of `Config.json` via `ErpSettings`; the **target** container-native model supplies the same keys as **environment variables / Kubernetes Secrets**. `Source: /WebVella.Erp/ErpSettings.cs:L29-L35,L99-L105`. They are documented **by key name and default only** — never place real credentials in documentation or configuration files.

| Key | Purpose | Default |
|-----|---------|---------|
| `EmailEnabled` | On/off switch for the **Web-layer direct-send path** (`MailService`) — **not** the SMTP queue (the queue is gated by the `smtp_service.is_enabled` record field). `Source: /WebVella.Erp.Web/Services/MailService.cs:L24` | `false` |
| `EmailSMTPServerName` | SMTP host name | — (required when enabled) |
| `EmailSMTPPort` | SMTP port | `25` |
| `EmailSMTPUsername` | SMTP authentication user | — (**secret** — provide via env var / Secret) |
| `EmailSMTPPassword` | SMTP authentication password | — (**secret** — provide via env var / Secret) |
| `EmailFrom` | Default *From* address | — |
| `EmailTo` | Default / testing *To* address | — |

Defaults are confirmed in code: `EmailEnabled` → `false` and `EmailSMTPPort` → `25`. `Source: /WebVella.Erp/ErpSettings.cs:L99,L101`

Secrets (`EmailSMTPUsername`, `EmailSMTPPassword`) must never be committed to source control: in the **current** model they are provided in the host's `Settings` configuration, and in the **target** container-native model through **environment variables / Kubernetes Secrets**.

Per-server behavior is configured as **`smtp_service` records** in the "mail" application: `server`, `port`, `connection_security`, `is_enabled`, `is_default`, `max_retries_count` and `retry_wait_minutes` govern how each SMTP server is used and how the queue retries. `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.20190215.cs:L580` See the consolidated [configuration reference](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/configuration-reference.md) for the full environment-variable / Secret list.

## Troubleshooting

The queue engine records the last error on each message and applies a retry-with-backoff policy before aborting. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L829-L878`

| Symptom | Likely cause | Remedy |
|---------|--------------|--------|
| Send fails; `server_error` is populated, `retries_count` increments and the message is re-queued | SMTP authentication or connection failure — the send path sets `server_error` to the exception message, increments `retries_count`, and re-queues (`status` → Pending, `scheduled_on = now + retry_wait_minutes`) until `retries_count >= max_retries_count`, after which `status` → Aborted. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L809-L819` | Verify SMTP host / port / credentials (by key name) and the server's TLS / `connection_security` selection. |
| TLS handshake or port errors | Wrong `connection_security` or `port` for the target server | Confirm the `smtp_service` record's `port` and connection-security value. |
| Messages stuck in the queue | An unresolved service reference sets `server_error = "SMTP service not found."` and aborts the message; otherwise messages wait for the retry backoff. `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L853-L861` | Inspect the `email` record's `server_error`; check `retry_wait_minutes` / `max_retries_count` on the `smtp_service`. |
| Queued messages never send | The target `smtp_service` record has `is_enabled = false` — the queue aborts with "SMTP service is not enabled". `Source: /WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs:L700-L702` | Enable the target `smtp_service` record (its `is_enabled` field). Note: `Settings:EmailEnabled` does **not** affect the queue — it only gates the separate `MailService` direct-send path. |
| Queue never drains | The queue only drains when the schedule plan runs `ProcessSmtpQueueJob` (~every 10 minutes). `Source: /WebVella.Erp.Plugins.Mail/MailPlugin.cs:L41-L82` | **Today** the schedule plan runs **in-process** via the core `ScheduleManager` loop inside the ERP host (registered by `SetSchedulePlans()`) — ensure that host is running. **Target:** the job moves to the planned `WebVella.Erp.Worker` host (**not yet built** — AAP §0.9.2). `Source: /WebVella.Erp/Jobs/SheduleManager.cs:L223`. |

For operational diagnostics across the platform, see the [operational troubleshooting guide](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/troubleshooting.md).

## Related documentation

> **Link note (publish order).** The `docs/**` links below are **absolute upstream URLs** pointing at the repository's default branch; they resolve **once the documentation set is published there** (pin them to a specific release tag for long-term stability). Until then, the same pages are available under the `docs/` folder of your local checkout.

- [Plugin SDK — `IErpPlugin` contract](https://github.com/WebVella/WebVella-ERP/blob/master/docs/plugin-sdk/ierplugin-contract.md)
- [Plugin migration guide (`ErpPlugin` → `IErpPlugin`)](https://github.com/WebVella/WebVella-ERP/blob/master/docs/migration/plugin-migration.md)
- [Background jobs / worker host](https://github.com/WebVella/WebVella-ERP/blob/master/docs/developer/background-jobs/overview.md)
- [Configuration reference (environment variables / Secrets)](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/configuration-reference.md)
- [Operational troubleshooting](https://github.com/WebVella/WebVella-ERP/blob/master/docs/deployment/troubleshooting.md)

## License

Apache-2.0 — see the [LICENSE](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt) file. `Source: /WebVella.Erp.Plugins.Mail/WebVella.Erp.Plugins.Mail.csproj:L10`
