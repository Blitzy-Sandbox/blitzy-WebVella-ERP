---
status: OPEN
phases:
  devops: OPEN
  security: OPEN
  backend: OPEN
  qa: OPEN
  business: OPEN
  frontend: OPEN
---

<!--
FRONTMATTER CONTRACT — DO NOT EDIT MANUALLY EXCEPT PER THE SEGMENTED PR REVIEW
RULE BELOW. The YAML block above is the sole authoritative source of review
status for this PR. Tools (grep, merge bots, CI gates) read these fields to
decide whether the PR may merge. Valid values per phase field:

    OPEN       — phase not yet started
    IN_REVIEW  — reviewer is actively auditing this phase
    BLOCKED    — reviewer documented findings; PR author must remediate
    APPROVED   — all sign-off criteria verified; phase is closed

The top-level `status:` field MUST equal `OPEN` until every enumerated phase
field reads `APPROVED`, at which point `status:` is set to `APPROVED`. If any
enumerated phase transitions to `BLOCKED`, `status:` is set to `BLOCKED` until
the phase is returned to `APPROVED`.

Adding a Phase 7 (Other SMEs) key is permitted — and required — when the
domain-scope determination in the Segmented PR Review Rule below concludes an
additional SME review is warranted. Add the key under `phases:` with initial
value `OPEN` and include a corresponding "Phase 7 — Other SMEs" section.
-->

# Code Review — Nx Monorepo Serverless Migration (PR `blitzy-28124201-2161-4a8d-a225-5250ade8f419`)

**PR scope summary:** Complete architectural rewrite of the WebVella ERP monolith (ASP.NET Core 9 + Razor Pages + PostgreSQL) into a serverless Nx monorepo comprising a React 19 SPA, 10 .NET 9 Native AOT Lambda services, a Node.js 22 Lambda authorizer, 4 shared libraries, AWS CDK infrastructure, and LocalStack-based integration tests (748 added/modified files).

---

## The Segmented PR Review Rule (Authoritative)

This review is governed by the **Segmented PR Review Rule**. The rule is stated in full here so every reviewer, PR author, and automation consumer can verify compliance without consulting any external document.

### R1. Scope Segmentation

A large-scale PR — defined as any PR that crosses two or more architectural boundaries (e.g., frontend + backend + infra, or backend + security + tests) — MUST be segmented into **phases**, where each phase is owned by exactly one subject-matter expert (SME) role. No file may appear as the primary review target of more than one phase. A file may be secondarily consulted by another phase's reviewer for context, but the authoritative sign-off for that file belongs to exactly one phase.

### R2. Sequential Execution (No Parallelization)

Phases MUST be executed in strict sequential order: Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 → Phase 6 → Phase 7 (when activated). Parallel execution of phases is FORBIDDEN. Phase N+1 is BLOCKED from starting until Phase N's frontmatter field reads `APPROVED`. This ordering exists because each successive phase depends on the invariants established by the preceding phase (e.g., QA cannot validate test reliability until the backend service boundaries have been signed off).

### R3. Explicit Entry and Exit Criteria

Every phase in this document has an **Entry Criteria** subsection and an **Exit Criteria** subsection. The Entry Criteria enumerate the frontmatter states and PR-level preconditions that MUST be satisfied before the reviewer may set the phase's frontmatter field to `IN_REVIEW`. The Exit Criteria enumerate the conditions that MUST all evaluate to true before the reviewer may set the phase's frontmatter field to `APPROVED`. If any Exit Criterion fails, the reviewer MUST set the phase's frontmatter field to `BLOCKED` and follow the FAIL STATE protocol.

### R4. Uniquely Numbered Domain-Specific Checks

Each phase's domain-specific checks are numbered `<phase_number>.<check_number>` (e.g., `1.1`, `1.2`, `2.1`, …). This numbering is stable across the PR's lifetime so that findings may be referenced precisely in commit messages, review comments, and changelog notes (e.g., "Resolved finding 4.3 — Integration tests no longer import `Moq<IAmazonDynamoDB>`").

### R5. FAIL STATE Protocol (Uniform Across All Phases)

If any Exit Criterion for a phase fails, the reviewer MUST:

1. Record every failing check by its unique number in the **Findings** field inside the phase's Sign-Off Block.
2. Set the phase's frontmatter field to `BLOCKED`.
3. Set the top-level `status:` frontmatter field to `BLOCKED`.
4. Notify the PR author with a direct link to the failing checks.
5. The PR author addresses all recorded findings via new commits on the same branch. Force-pushes to the review branch are FORBIDDEN.
6. The reviewer re-reviews ONLY the files changed since the `BLOCKED` transition (use `git diff <blocked-commit>..HEAD`), re-evaluates every recorded finding, and — only when every finding is resolved — sets the phase's frontmatter field to `APPROVED` and clears the top-level `status:` to `OPEN` (unless another phase is still blocked).
7. Phase N+1 MUST NOT begin under any circumstances until the BLOCKED phase returns to `APPROVED`.

### R6. Other SMEs Escalation (Phase 7)

If, during any phase, the reviewer identifies subject matter that lies outside the six standard phases (e.g., data engineering, ML ops, compliance, accessibility, internationalization, licensing), the reviewer MUST:

1. Add a new phase key to the frontmatter (e.g., `accessibility: OPEN`) with initial value `OPEN`.
2. Append a "Phase 7 — Other SMEs" section following the Phase 7 template below.
3. Document the scope, assigned SME role, domain-specific checks, and sign-off criteria.
4. Escalate sequentially: Phase 7 runs after Phase 6 completes; if multiple Phase 7 tracks are required, they also run sequentially (Phase 7a → Phase 7b).

### R7. Partial Sign-Off is Not Approval

A PR is merge-eligible only when every enumerated frontmatter phase field reads `APPROVED` and the top-level `status:` field reads `APPROVED`. Partial sign-off — any non-empty subset of phases in `APPROVED` with others in `OPEN`, `IN_REVIEW`, or `BLOCKED` — does NOT constitute PR approval. Any reviewer who submits an approving review on the PR under partial sign-off is in violation of this rule.

### R8. Gate Verification Commands

The following commands — runnable from the repository root — are the authoritative gate verifiers referenced by `README.md#pr-approval-process`:

```bash
# Must return no output for merge to be permitted:
grep -E "^\s+(devops|security|backend|qa|business|frontend):\s+BLOCKED" CODE_REVIEW.md

# Must print at least 6:
grep -cE "^\s+(devops|security|backend|qa|business|frontend):\s+APPROVED" CODE_REVIEW.md

# Inspect all phase frontmatter fields:
grep -A 20 "^phases:" CODE_REVIEW.md | head -40
```

---

## Phase 1 — DevOps Engineer

> **Phase Gate:** This phase is the first in the sequence. Entry is permitted unconditionally. All subsequent phases (2–6) are BLOCKED from starting until this phase's frontmatter field `devops` transitions to `APPROVED`.

### A. Reviewer Role

**Reviewer title:** DevOps Engineer (SME).

**Reviewer qualifications required:** Familiarity with GitHub Actions workflow syntax, Docker Compose authoring, AWS CDK v2 in TypeScript, Nx monorepo task graphs, LocalStack Pro Community edition feature matrix, and `localstack/setup-localstack` GitHub Action.

**Accountability:** CI/CD integrity, Infrastructure-as-Code correctness, container and monorepo configuration, and deployment/bootstrap script safety. Owns the pipeline from source to running environment.

### B. Entry Criteria

1. The PR branch `blitzy-28124201-2161-4a8d-a225-5250ade8f419` is pushed to origin and the Blitzy-generated file inventory (748 files) is complete.
2. The frontmatter field `devops` reads `OPEN` (no prior review session in progress).
3. The reviewer has read `README.md#pr-approval-process` and the Segmented PR Review Rule (R1–R8) above.

**Reviewer action on entry:** Set frontmatter `devops: IN_REVIEW`.

### C. Files to Review (106 files)

**CI/CD pipelines (3 files):**
- `.github/workflows/ci.yml`
- `.github/workflows/deploy.yml`
- `.github/workflows/e2e.yml`

**Container orchestration (1 file):**
- `docker-compose.yml`

**Monorepo & root build configuration (8 files):**
- `.blitzyignore`
- `.eslintrc.json`
- `.gitignore`
- `.prettierrc`
- `nx.json`
- `package-lock.json`
- `package.json`
- `tsconfig.base.json`

**Deployment & bootstrap scripts (4 files):**
- `tools/scripts/bootstrap-localstack.sh`
- `tools/scripts/e2e-mock-server.mjs`
- `tools/scripts/run-migrations.sh`
- `tools/scripts/seed-test-data.sh`

**CDK infrastructure — application & stacks (24 files):**
- `infra/cdk.context.json`
- `infra/cdk.json`
- `infra/package.json`
- `infra/project.json`
- `infra/src/app.ts`
- `infra/src/constructs/api-integration.ts`
- `infra/src/constructs/dynamodb-table.ts`
- `infra/src/constructs/event-bus.ts`
- `infra/src/constructs/index.ts`
- `infra/src/constructs/lambda-service.ts`
- `infra/src/stacks/api-gateway-stack.ts`
- `infra/src/stacks/crm-stack.ts`
- `infra/src/stacks/entity-management-stack.ts`
- `infra/src/stacks/file-management-stack.ts`
- `infra/src/stacks/frontend-stack.ts`
- `infra/src/stacks/identity-stack.ts`
- `infra/src/stacks/inventory-stack.ts`
- `infra/src/stacks/invoicing-stack.ts`
- `infra/src/stacks/notifications-stack.ts`
- `infra/src/stacks/plugin-system-stack.ts`
- `infra/src/stacks/reporting-stack.ts`
- `infra/src/stacks/shared-stack.ts`
- `infra/src/stacks/workflow-stack.ts`
- `infra/tsconfig.json`

**Shared CDK construct library (8 files):**
- `libs/shared-cdk-constructs/package.json`
- `libs/shared-cdk-constructs/project.json`
- `libs/shared-cdk-constructs/src/dynamodb-table.ts`
- `libs/shared-cdk-constructs/src/event-bus.ts`
- `libs/shared-cdk-constructs/src/index.ts`
- `libs/shared-cdk-constructs/src/lambda-service.ts`
- `libs/shared-cdk-constructs/tsconfig.json`
- `libs/shared-cdk-constructs/tsconfig.lib.json`

**Project documentation (58 files):**
- `README.md`
- `docs/executive-review.html`
- `blitzy/documentation/Project Guide.md`
- `blitzy/documentation/Technical Specifications.md`
- `blitzy/screenshots/CheckboxListField_all_scenarios_styled.png`
- `blitzy/screenshots/DataCsvField_runtime_bottom.png`
- `blitzy/screenshots/DataCsvField_runtime_top.png`
- `blitzy/screenshots/ImageField_all_scenarios_styled.png`
- `blitzy/screenshots/LogList_page_default_state.png`
- `blitzy/screenshots/LogList_search_drawer_open.png`
- `blitzy/screenshots/MultiSelectField_runtime_verification.png`
- `blitzy/screenshots/QuoteCreate_full_page.png`
- `blitzy/screenshots/TaskCreate_page_rendered.png`
- `blitzy/screenshots/TaskCreate_validation_errors.png`
- `blitzy/screenshots/admin_entity_data_create_form_rendered.png`
- `blitzy/screenshots/admin_entity_manage_full_page.png`
- `blitzy/screenshots/admin_entity_relation_manage_form.png`
- ... (45 additional screenshot assets captured during validation)

### D. Domain-Specific Checks

- **1.1** CI/CD step ordering — tests run before deploy; no deploy job depends on a skipped/failing test job.
- **1.2** Image versions pinned in `docker-compose.yml` and workflow files — no `:latest` tags for LocalStack or any other service image.
- **1.3** No plaintext secrets in any config file (`nx.json`, `package.json`, `docker-compose.yml`, `.github/workflows/*.yml`, `infra/cdk.json`, `infra/cdk.context.json`).
- **1.4** IaC validates without errors — `cd infra && npx tsc --noEmit` returns 0; `npx cdk synth --context localstack=true` produces a valid template without errors.
- **1.5** Build scripts idempotent — `tools/scripts/bootstrap-localstack.sh`, `run-migrations.sh`, and `seed-test-data.sh` can be re-executed safely without corrupting existing state.
- **1.6** Required environment variables documented in `README.md` (`AWS_ENDPOINT_URL`, `AWS_REGION`, `COGNITO_USER_POOL_ID`, `API_GATEWAY_URL`, `IS_LOCAL`, `VITE_API_URL`, `LOCALSTACK_AUTH_TOKEN`).
- **1.7** Nx workspace: `nx.json` defines task pipelines (`build`, `test`, `lint`, `e2e`) with caching; `project.json` files exist for every app, service, and lib.
- **1.8** `.gitignore` / `.blitzyignore` cover `node_modules/`, `.localstack/`, `volume/`, `localstack/`, `cdk.out/`, `*.env`, `.env.*`, `dist/`, `build/`, `coverage/`, `*.tfstate`.
- **1.9** CDK dual-target via `localstack` context flag — LocalStack-only resources (RDS stub, JWT authorizer fallback) are conditional, production-only resources (CloudFront, Route 53, ACM) are conditional.
- **1.10** GitHub Actions workflows reference the `localstack/setup-localstack` action for LocalStack-backed CI runs.

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `devops: APPROVED`:

- E1.A — All 10 domain-specific checks (1.1 through 1.10) passed.
- E1.B — `cd infra && npx tsc --noEmit` returns 0.
- E1.C — `cd libs/shared-cdk-constructs && npx tsc --noEmit` returns 0.
- E1.D — `docker compose -f docker-compose.yml config` validates without errors.
- E1.E — Every `.github/workflows/*.yml` file is syntactically valid YAML and uses `localstack/setup-localstack` for LocalStack-backed jobs.
- E1.F — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 1.3, 1.9):
______________
______________
______________

### G. FAIL STATE Protocol

If this phase is BLOCKED, follow R5 of the Segmented PR Review Rule above. Summary:

1. Record every failing check by its unique number (e.g., `1.3`, `1.9`) in the Findings field above.
2. Set frontmatter `devops: BLOCKED` and top-level `status: BLOCKED`.
3. Notify the PR author with a direct link to the failing check numbers.
4. PR author addresses findings via new commits (no force-push).
5. Reviewer re-reviews ONLY files changed since the BLOCKED transition and re-evaluates each recorded finding.
6. When every finding is resolved, set `devops: APPROVED` and clear top-level `status:` per R5.
7. **Phase 2 MUST NOT begin until `devops` reads `APPROVED`.**

---

## Phase 2 — Security Expert

> **Phase Gate:** This phase is BLOCKED from starting until frontmatter field `devops` reads `APPROVED`. Phases 3–6 are BLOCKED from starting until this phase's frontmatter field `security` reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** Security Expert (SME).

**Reviewer qualifications required:** Familiarity with AWS Cognito user pools and identity providers, OAuth 2.0 / OIDC / JWT (RS256 with JWKS), IAM policy authoring under least-privilege, AWS SSM Parameter Store SecureString, Lambda authorizer request/token authorization flows, OWASP Top 10, and DynamoDB expression-injection hardening.

**Accountability:** Authentication, authorization, token validation, IAM policies, secrets management, and the attack surface of identity-related Lambda handlers and the JWT authorizer.

### B. Entry Criteria

1. Frontmatter `devops: APPROVED` (Phase 1 complete).
2. Frontmatter `security: OPEN`.
3. The reviewer has read the Segmented PR Review Rule (R1–R8) and Phase 1's sign-off findings (to understand any DevOps-level security implications resolved upstream).

**Reviewer action on entry:** Set frontmatter `security: IN_REVIEW`.

### C. Files to Review (17 files)

**Identity service (12 files):**
- `services/identity/Identity.csproj`
- `services/identity/src/DataAccess/UserRepository.cs`
- `services/identity/src/Functions/AuthHandler.cs`
- `services/identity/src/Functions/RoleHandler.cs`
- `services/identity/src/Functions/UserHandler.cs`
- `services/identity/src/Models/Role.cs`
- `services/identity/src/Models/User.cs`
- `services/identity/src/Program.cs`
- `services/identity/src/Services/CognitoService.cs`
- `services/identity/src/Services/PermissionService.cs`
- `services/identity/src/project.json`
- `services/identity/src/triggers/user-migration/index.js`

**JWT Lambda authorizer (5 files):**
- `services/authorizer/package.json`
- `services/authorizer/src/index.ts`
- `services/authorizer/src/jwt-validator.ts`
- `services/authorizer/src/project.json`
- `services/authorizer/tsconfig.json`

### D. Domain-Specific Checks

- **2.1** No `alg:none` path — JWT verifier rejects tokens with algorithm `none`; only `RS256` (Cognito) and `HS256` (LocalStack dev-only) accepted.
- **2.2** Token expiry validated — `exp` claim checked with clock-skew tolerance; expired tokens rejected.
- **2.3** Deny-by-default authorization — authorizer returns `Deny` policy unless a valid JWT is proven; missing or malformed Authorization header rejected.
- **2.4** No wildcard IAM/RBAC grants — `PermissionService.cs` enumerates explicit permissions per role; no `Resource: '*'` or `Action: '*'` grants except for Lambda logs.
- **2.5** Secrets sourced from SSM Parameter Store `SecureString`, not environment variables; `DB_CONNECTION_STRING` and `COGNITO_CLIENT_SECRET` never appear in plaintext.
- **2.6** Unauthenticated routes enumerated and justified — only `/health` and `/v1/auth/login` bypass the authorizer; list explicitly declared in `infra/src/stacks/api-gateway-stack.ts`.
- **2.7** No sensitive fields in response schemas — password hashes, refresh tokens, and session IDs are never returned by `UserHandler`, `RoleHandler`, or `AuthHandler`.
- **2.8** Parameterized queries only — `UserRepository.cs` uses DynamoDB SDK with attribute values; zero string interpolation into DynamoDB expressions or SQL.
- **2.9** User-migration trigger (`services/identity/src/triggers/user-migration/index.js`) validates legacy MD5 hash with constant-time comparison (no timing oracle) and re-issues credentials through Cognito's secure hashing.
- **2.10** `jwt-validator.ts` uses `jwks-rsa` with cache + rate limit; JWKS key rotation supported without service restart.
- **2.11** CORS on identity endpoints locked to the frontend's documented origins (no `*`).
- **2.12** Input validation on `AuthHandler.Login` (email format, password length) and on `RoleHandler` / `UserHandler` POST/PUT bodies.

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `security: APPROVED`:

- E2.A — All 12 domain-specific checks (2.1 through 2.12) passed.
- E2.B — `dotnet build services/identity/Identity.csproj` succeeds with zero warnings.
- E2.C — `cd services/authorizer && npm run build` succeeds with zero errors.
- E2.D — Unit tests for `jwt-validator` and `CognitoService` achieve ≥ 80% branch coverage (verified in Phase 4; pre-checked here that spec files exist: `services/authorizer/tests/jwt-validator.test.ts`, `services/identity/tests/Unit/CognitoServiceTests.cs`).
- E2.E — No HIGH/CRITICAL findings remain from `npm audit --audit-level=high` on `services/authorizer/package.json`.
- E2.F — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 2.1, 2.9):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5 of the Segmented PR Review Rule. Record failing checks by number (e.g., `2.1`, `2.9`) in the Findings field; set `security: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `security: APPROVED` when resolved. **Phase 3 MUST NOT begin until `security` reads `APPROVED`.**

---

## Phase 3 — Backend Lead

> **Phase Gate:** This phase is BLOCKED from starting until frontmatter fields `devops` AND `security` both read `APPROVED`. Phases 4–6 are BLOCKED from starting until this phase's frontmatter field `backend` reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** Backend Lead (SME).

**Reviewer qualifications required:** Familiarity with .NET 9 Native AOT Lambda authoring, DynamoDB single-table design, RDS PostgreSQL + Npgsql + FluentMigrator migrations, event-driven architecture (SNS fan-out, SQS consumer patterns, DLQs), OpenAPI 3.1, JSON Schema, idempotency and correlation-ID propagation, and API Gateway v2 HTTP integration.

**Accountability:** Service boundaries, handler/business-logic/data-access layering, event schema correctness, API contract conformance, shared cross-service utilities, migration ordering, and correlation-ID propagation across services.

### B. Entry Criteria

1. Frontmatter `devops: APPROVED` AND `security: APPROVED`.
2. Frontmatter `backend: OPEN`.
3. The reviewer has confirmed no BLOCKED carry-over findings remain from Phase 1 or Phase 2.

**Reviewer action on entry:** Set frontmatter `backend: IN_REVIEW`.

### C. Files to Review (86 files)

**Shared API contracts — OpenAPI 3.1 specs (10 files):**
- `libs/shared-schemas/src/api/crm-api.yaml`
- `libs/shared-schemas/src/api/entity-management-api.yaml`
- `libs/shared-schemas/src/api/file-management-api.yaml`
- `libs/shared-schemas/src/api/identity-api.yaml`
- `libs/shared-schemas/src/api/inventory-api.yaml`
- `libs/shared-schemas/src/api/invoicing-api.yaml`
- `libs/shared-schemas/src/api/notifications-api.yaml`
- `libs/shared-schemas/src/api/plugin-system-api.yaml`
- `libs/shared-schemas/src/api/reporting-api.yaml`
- `libs/shared-schemas/src/api/workflow-api.yaml`

**Shared event schemas — JSON Schema (10 files):**
- `libs/shared-schemas/src/events/crm.events.json`
- `libs/shared-schemas/src/events/entity.events.json`
- `libs/shared-schemas/src/events/file.events.json`
- `libs/shared-schemas/src/events/identity.events.json`
- `libs/shared-schemas/src/events/invoicing.events.json`
- `libs/shared-schemas/src/events/notification.events.json`
- `libs/shared-schemas/src/events/plugin.events.json`
- `libs/shared-schemas/src/events/record.events.json`
- `libs/shared-schemas/src/events/relation.events.json`
- `libs/shared-schemas/src/events/workflow.events.json`

**Shared schemas package (5 files):**
- `libs/shared-schemas/package.json`
- `libs/shared-schemas/project.json`
- `libs/shared-schemas/src/index.ts`
- `libs/shared-schemas/tsconfig.json`
- `libs/shared-schemas/tsconfig.lib.json`

**Shared cross-service utilities (7 files):**
- `libs/shared-utils/package.json`
- `libs/shared-utils/project.json`
- `libs/shared-utils/src/correlation-id.ts`
- `libs/shared-utils/src/idempotency.ts`
- `libs/shared-utils/src/index.ts`
- `libs/shared-utils/src/logger.ts`
- `libs/shared-utils/tsconfig.lib.json`

**Entity Management service (46 files):**
- `services/entity-management/EntityManagement.csproj`
- `services/entity-management/src/DataAccess/EntityRepository.cs`
- `services/entity-management/src/DataAccess/RecordRepository.cs`
- `services/entity-management/src/Functions/DataSourceHandler.cs`
- `services/entity-management/src/Functions/EntityHandler.cs`
- `services/entity-management/src/Functions/FieldHandler.cs`
- `services/entity-management/src/Functions/ImportExportHandler.cs`
- `services/entity-management/src/Functions/RecordHandler.cs`
- `services/entity-management/src/Functions/RelationHandler.cs`
- `services/entity-management/src/Functions/SearchHandler.cs`
- `services/entity-management/src/Models/BaseModels.cs`
- `services/entity-management/src/Models/DataSourceModels.cs`
- `services/entity-management/src/Models/Definitions.cs`
- `services/entity-management/src/Models/Entity.cs`
- `services/entity-management/src/Models/EntityRecord.cs`
- `services/entity-management/src/Models/EntityRelation.cs`
- `services/entity-management/src/Models/Field.cs`
- `services/entity-management/src/Models/FieldTypes/AutoNumberField.cs`
- `services/entity-management/src/Models/FieldTypes/CheckboxField.cs`
- `services/entity-management/src/Models/FieldTypes/CurrencyField.cs`
- `services/entity-management/src/Models/FieldTypes/DateField.cs`
- `services/entity-management/src/Models/FieldTypes/DateTimeField.cs`
- `services/entity-management/src/Models/FieldTypes/EmailField.cs`
- `services/entity-management/src/Models/FieldTypes/FileField.cs`
- `services/entity-management/src/Models/FieldTypes/FormulaField.cs`
- `services/entity-management/src/Models/FieldTypes/GeographyField.cs`
- `services/entity-management/src/Models/FieldTypes/GuidField.cs`
- `services/entity-management/src/Models/FieldTypes/HtmlField.cs`
- `services/entity-management/src/Models/FieldTypes/ImageField.cs`
- `services/entity-management/src/Models/FieldTypes/MultiLineTextField.cs`
- `services/entity-management/src/Models/FieldTypes/MultiSelectField.cs`
- `services/entity-management/src/Models/FieldTypes/NumberField.cs`
- `services/entity-management/src/Models/FieldTypes/PasswordField.cs`
- `services/entity-management/src/Models/FieldTypes/PercentField.cs`
- `services/entity-management/src/Models/FieldTypes/PhoneField.cs`
- `services/entity-management/src/Models/FieldTypes/SelectField.cs`
- `services/entity-management/src/Models/FieldTypes/TextField.cs`
- `services/entity-management/src/Models/FieldTypes/TreeSelectField.cs`
- `services/entity-management/src/Models/FieldTypes/UrlField.cs`
- `services/entity-management/src/Models/QueryModels.cs`
- `services/entity-management/src/Models/SearchModels.cs`
- `services/entity-management/src/Program.cs`
- `services/entity-management/src/Services/EntityService.cs`
- `services/entity-management/src/Services/QueryAdapter.cs`
- `services/entity-management/src/Services/RecordService.cs`
- `services/entity-management/src/project.json`

**Plugin System service (8 files):**
- `services/plugin-system/PluginSystem.csproj`
- `services/plugin-system/src/DataAccess/PluginRepository.cs`
- `services/plugin-system/src/Functions/PluginHandler.cs`
- `services/plugin-system/src/Models/Plugin.cs`
- `services/plugin-system/src/Program.cs`
- `services/plugin-system/src/Services/PluginService.cs`
- `services/plugin-system/src/Services/SitemapService.cs`
- `services/plugin-system/src/project.json`

### D. Domain-Specific Checks

- **3.1** No cross-service internal imports — no `services/X/src/**` file imports from `services/Y/src/**`. Cross-service communication must flow through published OpenAPI endpoints or JSON Schema events only.
- **3.2** Handlers (`services/*/src/Functions/*Handler.cs`) contain zero business logic — handlers deserialize input, call a service method, serialize output, and handle errors. Business rules belong in `Services/`.
- **3.3** Repositories (`services/*/src/DataAccess/*Repository.cs`) contain zero business logic — pure persistence operations (get, put, query, delete, update); validation and rule evaluation occur in `Services/`.
- **3.4** Event payloads published by any service match the corresponding JSON Schema in `libs/shared-schemas/src/events/*.json`; every emitted SNS event validates against its schema.
- **3.5** Migrations ordered and idempotent — `services/invoicing/src/Migrations/InitialCreate.cs` and `services/reporting/src/Migrations/Migration_001_InitialSchema.cs` use FluentMigrator with explicit version numbers and re-runnable statements.
- **3.6** No hardcoded resource IDs or connection strings — all table names, bucket names, queue ARNs, topic ARNs come from environment variables or SSM parameters sourced in `Program.cs`.
- **3.7** Correlation IDs propagated — every outbound call (SNS publish, SQS send, HTTP invoke) includes `X-Correlation-Id` from the incoming Lambda event context via `libs/shared-utils/src/correlation-id.ts`.
- **3.8** OpenAPI specs in `libs/shared-schemas/src/api/*.yaml` match the actual routes declared in API Gateway stack and implemented in Lambda handlers; contract drift detected via contract tests.
- **3.9** Shared utilities (`libs/shared-utils`) are pure and free of service-specific logic — `logger`, `correlation-id`, and `idempotency` modules exported without bleed of domain types.
- **3.10** Entity Management owns all entity/field/relation metadata — no other service reads or writes the entity-metadata DynamoDB table; other services call Entity Management's API.
- **3.11** Plugin System owns plugin registry — plugin metadata persisted in a dedicated DynamoDB table; no cross-service access to plugin data.
- **3.12** 20+ field type classes in `services/entity-management/src/Models/FieldTypes/` preserve behavioral parity with the monolith (`WebVella.Erp/Database/FieldTypes/`).

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `backend: APPROVED`:

- E3.A — All 12 domain-specific checks (3.1 through 3.12) passed.
- E3.B — `dotnet build services/entity-management/EntityManagement.csproj` succeeds with zero warnings.
- E3.C — `dotnet build services/plugin-system/PluginSystem.csproj` succeeds with zero warnings.
- E3.D — `cd libs/shared-schemas && npx tsc --noEmit` returns 0.
- E3.E — `cd libs/shared-utils && npx tsc --noEmit` returns 0.
- E3.F — All 10 OpenAPI specs validate (`npx @redocly/cli lint libs/shared-schemas/src/api/*.yaml` returns 0).
- E3.G — All 10 JSON Schema event documents parse as valid JSON Schema (Draft 2020-12).
- E3.H — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 3.1, 3.8):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5. Record failing checks by number (e.g., `3.1`, `3.8`) in the Findings field; set `backend: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `backend: APPROVED` when resolved. **Phase 4 MUST NOT begin until `backend` reads `APPROVED`.**

---

## Phase 4 — QA Engineer

> **Phase Gate:** This phase is BLOCKED from starting until frontmatter fields `devops`, `security`, AND `backend` all read `APPROVED`. Phases 5–6 are BLOCKED from starting until this phase's frontmatter field `qa` reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** QA Engineer (SME).

**Reviewer qualifications required:** Familiarity with xUnit + `FluentAssertions` + `Moq`, Vitest + `@testing-library/react`, Playwright 1.x, LocalStack Community vs Pro feature parity (so `CognitoFactAttribute` / `RdsFactAttribute` skip-semantics are understood), fixture lifecycle (`IAsyncLifetime`, `IDisposable`, xUnit `ICollectionFixture`), and code-coverage tooling (`coverlet.collector`, `vitest --coverage`).

**Accountability:** Test completeness, test correctness, reliability against LocalStack, cross-test isolation, fixture cleanliness, and coverage of new code paths and critical user journeys introduced by this PR.

### B. Entry Criteria

1. Frontmatter `devops`, `security`, `backend` all read `APPROVED`.
2. Frontmatter `qa: OPEN`.
3. LocalStack Community is running locally (or in CI) for integration tests, or the reviewer has confirmed that Pro-dependent tests are appropriately skipped via `CognitoFactAttribute` / `RdsFactAttribute` when Pro is unavailable (per the setup-status note documenting LocalStack Pro licence expiry after 2026-03-01).

**Reviewer action on entry:** Set frontmatter `qa: IN_REVIEW`.

### C. Files to Review (190 files)

**Frontend E2E (Playwright) (17 files):**
- `apps/frontend-e2e/playwright.config.ts`
- `apps/frontend-e2e/project.json`
- `apps/frontend-e2e/src/admin.spec.ts`
- `apps/frontend-e2e/src/auth.spec.ts`
- `apps/frontend-e2e/src/crm.spec.ts`
- `apps/frontend-e2e/src/dashboard.spec.ts`
- `apps/frontend-e2e/src/files.spec.ts`
- `apps/frontend-e2e/src/navigation.spec.ts`
- `apps/frontend-e2e/src/notifications.spec.ts`
- `apps/frontend-e2e/src/projects.spec.ts`
- `apps/frontend-e2e/src/records.spec.ts`
- `apps/frontend/tests/e2e/admin.spec.ts`
- `apps/frontend/tests/e2e/auth.spec.ts`
- `apps/frontend/tests/e2e/crm.spec.ts`
- `apps/frontend/tests/e2e/navigation.spec.ts`
- `apps/frontend/tests/e2e/projects.spec.ts`
- `apps/frontend/tests/e2e/records.spec.ts`

**Frontend unit tests (Vitest) (61 files):**
- `apps/frontend/tests/unit/common/Button.test.tsx`
- `apps/frontend/tests/unit/common/Chart.test.tsx`
- `apps/frontend/tests/unit/common/Drawer.test.tsx`
- `apps/frontend/tests/unit/common/Modal.test.tsx`
- `apps/frontend/tests/unit/common/TabNav.test.tsx`
- `apps/frontend/tests/unit/data-table/DataTable.test.tsx`
- `apps/frontend/tests/unit/fields/AutonumberField.test.tsx`
- `apps/frontend/tests/unit/fields/CheckboxField.test.tsx`
- `apps/frontend/tests/unit/fields/CheckboxGridField.test.tsx`
- `apps/frontend/tests/unit/fields/CheckboxListField.test.tsx`
- `apps/frontend/tests/unit/fields/CodeField.test.tsx`
- `apps/frontend/tests/unit/fields/ColorField.test.tsx`
- `apps/frontend/tests/unit/fields/CurrencyField.test.tsx`
- `apps/frontend/tests/unit/fields/DataCsvField.test.tsx`
- `apps/frontend/tests/unit/fields/DateField.test.tsx`
- `apps/frontend/tests/unit/fields/DateTimeField.test.tsx`
- `apps/frontend/tests/unit/fields/EmailField.test.tsx`
- `apps/frontend/tests/unit/fields/FileField.test.tsx`
- `apps/frontend/tests/unit/fields/GuidField.test.tsx`
- `apps/frontend/tests/unit/fields/HiddenField.test.tsx`
- `apps/frontend/tests/unit/fields/HtmlField.test.tsx`
- `apps/frontend/tests/unit/fields/IconField.test.tsx`
- `apps/frontend/tests/unit/fields/ImageField.test.tsx`
- `apps/frontend/tests/unit/fields/MultiFileUploadField.test.tsx`
- `apps/frontend/tests/unit/fields/MultiSelectField.test.tsx`
- `apps/frontend/tests/unit/fields/NumberField.test.tsx`
- `apps/frontend/tests/unit/fields/PasswordField.test.tsx`
- `apps/frontend/tests/unit/fields/PercentField.test.tsx`
- `apps/frontend/tests/unit/fields/PhoneField.test.tsx`
- `apps/frontend/tests/unit/fields/RadioListField.test.tsx`
- `apps/frontend/tests/unit/fields/SelectField.test.tsx`
- `apps/frontend/tests/unit/fields/TextField.test.tsx`
- `apps/frontend/tests/unit/fields/TextareaField.test.tsx`
- `apps/frontend/tests/unit/fields/TimeField.test.tsx`
- `apps/frontend/tests/unit/fields/UrlField.test.tsx`
- `apps/frontend/tests/unit/forms/DynamicForm.test.tsx`
- `apps/frontend/tests/unit/hooks/useApps.test.ts`
- `apps/frontend/tests/unit/hooks/useAuth.test.ts`
- `apps/frontend/tests/unit/hooks/useCrm.test.ts`
- `apps/frontend/tests/unit/hooks/useEntities.test.ts`
- `apps/frontend/tests/unit/hooks/useFiles.test.ts`
- `apps/frontend/tests/unit/hooks/useNotifications.test.ts`
- `apps/frontend/tests/unit/hooks/usePages.test.ts`
- `apps/frontend/tests/unit/hooks/usePlugins.test.ts`
- `apps/frontend/tests/unit/hooks/useProjects.test.ts`
- `apps/frontend/tests/unit/hooks/useRecords.test.ts`
- `apps/frontend/tests/unit/hooks/useReports.test.ts`
- `apps/frontend/tests/unit/hooks/useSearch.test.ts`
- `apps/frontend/tests/unit/hooks/useUsers.test.ts`
- `apps/frontend/tests/unit/hooks/useWorkflows.test.ts`
- `apps/frontend/tests/unit/layout/AppShell.test.tsx`
- `apps/frontend/tests/unit/layout/Breadcrumb.test.tsx`
- `apps/frontend/tests/unit/layout/Sidebar.test.tsx`
- `apps/frontend/tests/unit/layout/TopNav.test.tsx`
- `apps/frontend/tests/unit/stores/appStore.test.ts`
- `apps/frontend/tests/unit/stores/authStore.test.ts`
- `apps/frontend/tests/unit/stores/pageBuilderStore.test.ts`
- `apps/frontend/tests/unit/stores/uiStore.test.ts`
- `apps/frontend/tests/unit/utils/constants.test.ts`
- `apps/frontend/tests/unit/utils/formatters.test.ts`
- `apps/frontend/tests/unit/utils/validators.test.ts`

**Authorizer unit tests (Vitest) (2 files):**
- `services/authorizer/tests/index.test.ts`
- `services/authorizer/tests/jwt-validator.test.ts`

**Service unit tests (.NET) (48 files):**
- `services/crm/tests/AccountHandlerTests.cs`
- `services/crm/tests/ContactHandlerTests.cs`
- `services/crm/tests/ContractTests.cs`
- `services/crm/tests/SearchServiceTests.cs`
- `services/entity-management/tests/Unit/DataAccess/EntityRepositoryTests.cs`
- `services/entity-management/tests/Unit/DataAccess/RecordRepositoryTests.cs`
- `services/entity-management/tests/Unit/Functions/DataSourceHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/EntityHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/FieldHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/ImportExportHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/RecordHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/RelationHandlerTests.cs`
- `services/entity-management/tests/Unit/Functions/SearchHandlerTests.cs`
- `services/entity-management/tests/Unit/Services/EntityServiceTests.cs`
- `services/entity-management/tests/Unit/Services/QueryAdapterTests.cs`
- `services/entity-management/tests/Unit/Services/RecordServiceTests.cs`
- `services/file-management/tests/DownloadHandlerTests.cs`
- `services/file-management/tests/FileMetadataRepositoryTests.cs`
- `services/file-management/tests/S3ServiceTests.cs`
- `services/file-management/tests/UploadHandlerTests.cs`
- `services/identity/tests/Unit/AuthHandlerTests.cs`
- `services/identity/tests/Unit/CognitoServiceTests.cs`
- `services/identity/tests/Unit/PermissionServiceTests.cs`
- `services/identity/tests/Unit/RoleHandlerTests.cs`
- `services/identity/tests/Unit/UserHandlerTests.cs`
- `services/identity/tests/Unit/UserRepositoryTests.cs`
- `services/inventory/tests/Unit/InventoryRepositoryTests.cs`
- `services/inventory/tests/Unit/TaskServiceTests.cs`
- `services/inventory/tests/Unit/TimelogHandlerTests.cs`
- `services/invoicing/tests/Unit/InvoiceEventPublisherTests.cs`
- `services/invoicing/tests/Unit/InvoiceServiceTests.cs`
- `services/invoicing/tests/Unit/LineItemCalculationServiceTests.cs`
- `services/invoicing/tests/Unit/PaymentServiceTests.cs`
- `services/invoicing/tests/Unit/TaxCalculationServiceTests.cs`
- `services/notifications/tests/EmailHandlerTests.cs`
- `services/notifications/tests/QueueProcessorTests.cs`
- `services/notifications/tests/SmtpServiceTests.cs`
- `services/notifications/tests/WebhookHandlerTests.cs`
- `services/plugin-system/tests/Unit/PluginHandlerTests.cs`
- `services/plugin-system/tests/Unit/PluginModelTests.cs`
- `services/plugin-system/tests/Unit/PluginServiceTests.cs`
- `services/reporting/tests/Unit/EventConsumerTests.cs`
- `services/reporting/tests/Unit/ProjectionServiceTests.cs`
- `services/reporting/tests/Unit/ReportHandlerTests.cs`
- `services/reporting/tests/Unit/ReportServiceTests.cs`
- `services/workflow/tests/Unit/StepHandlerTests.cs`
- `services/workflow/tests/Unit/WorkflowHandlerTests.cs`
- `services/workflow/tests/Unit/WorkflowServiceTests.cs`

**Service integration tests (.NET) (32 files):**
- `services/crm/tests/CrmRepositoryIntegrationTests.cs`
- `services/entity-management/tests/Integration/EntityCrudIntegrationTests.cs`
- `services/entity-management/tests/Integration/ImportExportIntegrationTests.cs`
- `services/entity-management/tests/Integration/QueryAdapterIntegrationTests.cs`
- `services/entity-management/tests/Integration/RecordCrudIntegrationTests.cs`
- `services/entity-management/tests/Integration/SearchIntegrationTests.cs`
- `services/file-management/tests/FileLifecycleIntegrationTests.cs`
- `services/file-management/tests/S3IntegrationTests.cs`
- `services/identity/tests/Integration/AuthFlowIntegrationTests.cs`
- `services/identity/tests/Integration/DynamoDbPersistenceIntegrationTests.cs`
- `services/identity/tests/Integration/RoleCrudIntegrationTests.cs`
- `services/identity/tests/Integration/UserCrudIntegrationTests.cs`
- `services/identity/tests/Integration/UserMigrationIntegrationTests.cs`
- `services/inventory/tests/Integration/SnsEventPublishingTests.cs`
- `services/inventory/tests/Integration/StepFunctionsIntegrationTests.cs`
- `services/inventory/tests/Integration/TaskHandlerIntegrationTests.cs`
- `services/inventory/tests/Integration/TimelogHandlerIntegrationTests.cs`
- `services/invoicing/tests/Integration/EventPublishingIntegrationTests.cs`
- `services/invoicing/tests/Integration/InvoiceLifecycleIntegrationTests.cs`
- `services/invoicing/tests/Integration/InvoiceRepositoryIntegrationTests.cs`
- `services/invoicing/tests/Integration/MigrationIntegrationTests.cs`
- `services/notifications/tests/NotificationRepositoryIntegrationTests.cs`
- `services/plugin-system/tests/Integration/PluginLifecycleIntegrationTests.cs`
- `services/plugin-system/tests/Integration/PluginRepositoryIntegrationTests.cs`
- `services/reporting/tests/Integration/EventConsumerIntegrationTests.cs`
- `services/reporting/tests/Integration/MigrationIntegrationTests.cs`
- `services/reporting/tests/Integration/ReportExecutionIntegrationTests.cs`
- `services/reporting/tests/Integration/ReportRepositoryIntegrationTests.cs`
- `services/workflow/tests/Integration/DynamoDbPersistenceTests.cs`
- `services/workflow/tests/Integration/SnsEventPublishingTests.cs`
- `services/workflow/tests/Integration/SqsTriggerTests.cs`
- `services/workflow/tests/Integration/StepFunctionsExecutionTests.cs`

**Service test fixtures & helpers (13 files):**
- `services/entity-management/tests/Fixtures/LocalStackFixture.cs`
- `services/entity-management/tests/Fixtures/TestDataHelper.cs`
- `services/identity/tests/Integration/CognitoFactAttribute.cs`
- `services/identity/tests/Integration/LocalStackFixture.cs`
- `services/inventory/tests/Integration/LocalStackFixture.cs`
- `services/invoicing/tests/Integration/InvoicingIntegrationCollection.cs`
- `services/invoicing/tests/Integration/LocalStackFixture.cs`
- `services/invoicing/tests/Integration/RdsFactAttribute.cs`
- `services/plugin-system/tests/Integration/LocalStackFixture.cs`
- `services/reporting/tests/Integration/DatabaseFixture.cs`
- `services/reporting/tests/Integration/LocalStackFixture.cs`
- `services/reporting/tests/Integration/RdsFactAttribute.cs`
- `services/reporting/tests/Integration/ReportingIntegrationCollection.cs`

**Service test project files (.csproj) (10 files):**
- `services/crm/tests/Crm.Tests.csproj`
- `services/entity-management/tests/EntityManagement.Tests.csproj`
- `services/file-management/tests/FileManagement.Tests.csproj`
- `services/identity/tests/Identity.Tests.csproj`
- `services/inventory/tests/Inventory.Tests.csproj`
- `services/invoicing/tests/Invoicing.Tests.csproj`
- `services/notifications/tests/Notifications.Tests.csproj`
- `services/plugin-system/tests/PluginSystem.Tests.csproj`
- `services/reporting/tests/Reporting.Tests.csproj`
- `services/workflow/tests/Workflow.Tests.csproj`

**Test configuration (6 files):**
- `apps/frontend/tsconfig.spec.json`
- `apps/frontend/vitest.config.ts`
- `libs/shared-schemas/vitest.config.ts`
- `libs/shared-ui/vitest.config.ts`
- `services/authorizer/vitest.config.ts`
- `services/entity-management/tests/xunit.runner.json`

_Total: 189 files classified in QA phase. (Previous duplicate `services/workflow/tests/WorkflowTests.csproj` orphan was removed in commit `b8a28e29`; `Workflow.Tests.csproj` is the sole authoritative test project for the workflow service.)_

### D. Domain-Specific Checks

- **4.1** Zero failures and zero skipped tests with CI artifact attached — test run produces a machine-readable report (TRX for .NET, JUnit XML for Vitest/Playwright) uploaded as a CI workflow artifact. Skips permitted only via `CognitoFactAttribute` / `RdsFactAttribute` when LocalStack Pro is unavailable; such skips are documented and do not count as failures.
- **4.2** Integration tests hit real dependencies (zero mock/stub matches for infrastructure) — grep for `Mock<IAmazonDynamoDB>`, `Mock<IAmazonS3>`, etc. in `tests/Integration/` returns zero results; real AWS SDK clients instantiated against LocalStack endpoint.
- **4.3** Teardown prevents cross-test state leakage — every `LocalStackFixture.cs` / `DatabaseFixture.cs` implements `IDisposable` or `IAsyncLifetime.DisposeAsync` that cleans up DynamoDB items, S3 objects, SQS queues, SNS topics, Cognito users, and RDS schemas created during tests.
- **4.4** New code paths have coverage — every handler, service method, and repository method added to the services covered in Phases 2, 3, and 5 has at least one unit test and, for integration-critical paths, at least one integration test.
- **4.5** E2E covers critical journeys introduced by PR — login, record CRUD, CRM contact creation, project task workflow, notifications, admin console, navigation — all have Playwright specs in `apps/frontend-e2e/src/` or `apps/frontend/tests/e2e/`.
- **4.6** Test project files (`*.Tests.csproj`) reference correct SDK `Microsoft.NET.Sdk` with `IsPackable=false` and target `net9.0`. The workflow service has exactly one authoritative test project: `services/workflow/tests/Workflow.Tests.csproj` (the duplicate `WorkflowTests.csproj` orphan was removed).
- **4.7** Integration tests that require LocalStack Pro services (`cognito-idp`, `rds`) are attributed with `CognitoFactAttribute` / `RdsFactAttribute` so they are skipped (not failed) when the Pro licence is unavailable; tests document the skip reason.
- **4.8** Test fixtures seed deterministic test data — no reliance on unspecified order; tests run correctly under parallel execution or are serialized via xUnit collection fixtures (`IntegrationCollection`).
- **4.9** Frontend unit tests render components with `@testing-library/react` and assert on user-facing behavior, not implementation details. All 61 field/layout/hook/store/util tests pass under Vitest run.
- **4.10** `vitest.config.ts` in each project uses `nxViteTsPaths()` plugin or equivalent path resolution so shared library imports resolve during tests.
- **4.11** No test contains a commented-out assertion, hardcoded production URL, or real AWS credentials.
- **4.12** Contract tests (e.g., `services/crm/tests/ContractTests.cs`) validate request/response schemas against the corresponding OpenAPI definition in `libs/shared-schemas/src/api/`.

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `qa: APPROVED`:

- E4.A — All 12 domain-specific checks (4.1 through 4.12) passed.
- E4.B — `dotnet test` returns `Passed!` for every service test project (unit + integration that do not require Pro services).
- E4.C — `npx vitest run` returns 0 failures for `apps/frontend`, `libs/shared-ui`, `libs/shared-schemas`, and `services/authorizer`.
- E4.D — `npx playwright test --project=chromium --list` enumerates all declared E2E specs without error.
- E4.E — New-code coverage ≥ 80% (line) for each modified service (`dotnet test /p:CollectCoverage=true`).
- E4.F — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 4.2, 4.7):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5. Record failing checks by number (e.g., `4.2`, `4.7`) in the Findings field; set `qa: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `qa: APPROVED` when resolved. **Phase 5 MUST NOT begin until `qa` reads `APPROVED`.**

---

## Phase 5 — Business / Domain Expert

> **Phase Gate:** This phase is BLOCKED from starting until frontmatter fields `devops`, `security`, `backend`, AND `qa` all read `APPROVED`. Phase 6 is BLOCKED from starting until this phase's frontmatter field `business` reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** Business / Domain Expert (SME).

**Reviewer qualifications required:** Subject-matter ownership of at least one of the seven bounded contexts (CRM, Inventory/Project Management, Invoicing, Reporting, Notifications, File Management, Workflow); familiarity with the monolith's behavioral parity requirement (AAP §0.8 — "Full behavioral parity"); comfort reading Step Functions ASL JSON; familiarity with FluentMigrator and schema-level isolation.

**Accountability:** Domain correctness of the seven bounded-context services, acceptance-criteria coverage, backward compatibility of model changes, and alignment of implemented workflows with the monolith's behavioral parity requirement.

### B. Entry Criteria

1. Frontmatter `devops`, `security`, `backend`, `qa` all read `APPROVED`.
2. Frontmatter `business: OPEN`.
3. The reviewer has retrieved the monolith reference code-paths from `WebVella.Erp/*` and `WebVella.Erp.Plugins.*` for behavioral-parity comparison.

**Reviewer action on entry:** Set frontmatter `business: IN_REVIEW`.

### C. Files to Review (98 files)

**CRM service (9 files):**
- `services/crm/Crm.csproj`
- `services/crm/src/DataAccess/CrmRepository.cs`
- `services/crm/src/Functions/AccountHandler.cs`
- `services/crm/src/Functions/ContactHandler.cs`
- `services/crm/src/Models/Account.cs`
- `services/crm/src/Models/Contact.cs`
- `services/crm/src/Program.cs`
- `services/crm/src/Services/SearchService.cs`
- `services/crm/src/project.json`

**Inventory / Project Management service (16 files):**
- `services/inventory/Inventory.csproj`
- `services/inventory/src/DataAccess/InventoryRepository.cs`
- `services/inventory/src/Functions/TaskHandler.cs`
- `services/inventory/src/Functions/TimelogHandler.cs`
- `services/inventory/src/Models/Comment.cs`
- `services/inventory/src/Models/FeedItem.cs`
- `services/inventory/src/Models/Project.cs`
- `services/inventory/src/Models/ResponseModel.cs`
- `services/inventory/src/Models/Task.cs`
- `services/inventory/src/Models/TaskStatus.cs`
- `services/inventory/src/Models/TaskType.cs`
- `services/inventory/src/Models/TasksDueType.cs`
- `services/inventory/src/Models/Timelog.cs`
- `services/inventory/src/Program.cs`
- `services/inventory/src/Services/TaskService.cs`
- `services/inventory/src/project.json`

**Invoicing service (RDS PostgreSQL) (17 files):**
- `services/invoicing/Invoicing.csproj`
- `services/invoicing/src/DataAccess/InvoiceRepository.cs`
- `services/invoicing/src/Functions/InvoiceHandler.cs`
- `services/invoicing/src/Functions/PaymentHandler.cs`
- `services/invoicing/src/Migrations/InitialCreate.cs`
- `services/invoicing/src/Models/BaseModels.cs`
- `services/invoicing/src/Models/Invoice.cs`
- `services/invoicing/src/Models/Payment.cs`
- `services/invoicing/src/Models/RequestModels.cs`
- `services/invoicing/src/Models/ResponseModels.cs`
- `services/invoicing/src/Program.cs`
- `services/invoicing/src/Services/InvoiceEventPublisher.cs`
- `services/invoicing/src/Services/InvoiceService.cs`
- `services/invoicing/src/Services/LineItemCalculationService.cs`
- `services/invoicing/src/Services/PaymentService.cs`
- `services/invoicing/src/Services/TaxCalculationService.cs`
- `services/invoicing/src/project.json`

**Reporting service (RDS PostgreSQL) (14 files):**
- `services/reporting/Reporting.csproj`
- `services/reporting/src/DataAccess/ReportRepository.cs`
- `services/reporting/src/Functions/EventConsumer.cs`
- `services/reporting/src/Functions/ReportHandler.cs`
- `services/reporting/src/Migrations/Migration_001_InitialSchema.cs`
- `services/reporting/src/Models/DomainEvent.cs`
- `services/reporting/src/Models/ReadModelProjection.cs`
- `services/reporting/src/Models/ReportDefinition.cs`
- `services/reporting/src/Models/ReportParameter.cs`
- `services/reporting/src/Models/ReportResult.cs`
- `services/reporting/src/Program.cs`
- `services/reporting/src/Services/ProjectionService.cs`
- `services/reporting/src/Services/ReportService.cs`
- `services/reporting/src/project.json`

**Notifications service (16 files):**
- `services/notifications/Notifications.csproj`
- `services/notifications/src/DataAccess/NotificationRepository.cs`
- `services/notifications/src/Functions/EmailHandler.cs`
- `services/notifications/src/Functions/QueueProcessor.cs`
- `services/notifications/src/Functions/WebhookHandler.cs`
- `services/notifications/src/Models/Email.cs`
- `services/notifications/src/Models/EmailAddress.cs`
- `services/notifications/src/Models/EmailPriority.cs`
- `services/notifications/src/Models/EmailStatus.cs`
- `services/notifications/src/Models/Notification.cs`
- `services/notifications/src/Models/NotificationEvent.cs`
- `services/notifications/src/Models/SmtpServiceConfig.cs`
- `services/notifications/src/Models/WebhookConfig.cs`
- `services/notifications/src/Program.cs`
- `services/notifications/src/Services/SmtpService.cs`
- `services/notifications/src/project.json`

**File Management service (10 files):**
- `services/file-management/FileManagement.csproj`
- `services/file-management/src/DataAccess/FileMetadataRepository.cs`
- `services/file-management/src/Functions/DownloadHandler.cs`
- `services/file-management/src/Functions/UploadHandler.cs`
- `services/file-management/src/Models/FileMetadata.cs`
- `services/file-management/src/Models/FileRequests.cs`
- `services/file-management/src/Models/FileResponses.cs`
- `services/file-management/src/Program.cs`
- `services/file-management/src/Services/S3Service.cs`
- `services/file-management/src/project.json`

**Workflow Engine service (16 files):**
- `services/workflow/Workflow.csproj`
- `services/workflow/src/Functions/StepHandler.cs`
- `services/workflow/src/Functions/WorkflowHandler.cs`
- `services/workflow/src/Models/SchedulePlan.cs`
- `services/workflow/src/Models/StepContext.cs`
- `services/workflow/src/Models/Workflow.cs`
- `services/workflow/src/Models/WorkflowSettings.cs`
- `services/workflow/src/Models/WorkflowType.cs`
- `services/workflow/src/Program.cs`
- `services/workflow/src/Services/WorkflowService.cs`
- `services/workflow/src/StateMachines/approval-chain.json`
- `services/workflow/src/StateMachines/daily-schedule.json`
- `services/workflow/src/StateMachines/interval-schedule.json`
- `services/workflow/src/StateMachines/monthly-schedule.json`
- `services/workflow/src/StateMachines/weekly-schedule.json`
- `services/workflow/src/project.json`

_Total: 98 files._

### D. Domain-Specific Checks

- **5.1** Workflows match PR acceptance criteria — CRM account/contact CRUD, project task creation and timelog recording, invoice creation with line items and tax, payment processing, email queue processor, S3 upload/download with presigned URLs, workflow state machine execution — all implemented end-to-end.
- **5.2** Model changes backward-compatible or migration documented — per-entity field shapes in `Account.cs`, `Contact.cs`, `Invoice.cs`, `Payment.cs`, `Email.cs`, `Task.cs`, `Timelog.cs`, `FileMetadata.cs` preserve the monolith's JSON schema or document a migration path.
- **5.3** Business rules match spec — `InvoiceService`, `LineItemCalculationService`, `TaxCalculationService`, `PaymentService` implement the monolith's invoicing rules (VAT, rounding, currency handling, totals).
- **5.4** UI flows match designs — page-level React components in `apps/frontend/src/pages/crm`, `/projects`, `/invoicing`, `/notifications`, `/files`, `/workflows`, `/reports` invoke the correct domain endpoints with correct payloads.
- **5.5** Deprecated features flagged for sign-off — any monolith feature intentionally omitted (e.g., Bulgarian FTS per AAP §0.3.2) is explicitly documented here and requires domain-owner approval.
- **5.6** CRM service owns account, contact, and address entities exclusively; no other service reads or writes these tables. CRM `SearchService.cs` regenerates `x_search` token fields on create/update.
- **5.7** Invoicing service uses RDS PostgreSQL with FluentMigrator migrations (`services/invoicing/src/Migrations/InitialCreate.cs`); transaction boundaries preserve invoice/line-item/payment ACID invariants.
- **5.8** Reporting service consumes domain events via SQS and projects them into RDS PostgreSQL read models (`ProjectionService.cs`, `EventConsumer.cs`); migrations ordered (`Migration_001_InitialSchema.cs`).
- **5.9** Notifications service preserves SMTP queue semantics (`SmtpService.cs`, `QueueProcessor.cs`) — retry with backoff, priority ordering, and at-least-once delivery.
- **5.10** File Management uses S3 for storage and DynamoDB for metadata; presigned URLs have appropriate TTLs; content-type and size limits enforced.
- **5.11** Workflow engine uses Step Functions state-machine definitions (`approval-chain.json`, `daily-schedule.json`, `interval-schedule.json`, `monthly-schedule.json`, `weekly-schedule.json`) that replicate the monolith's `SheduleManager` recurrence semantics.
- **5.12** Inventory service (project management) preserves the monolith's task/timelog/comment/feed semantics with `TaskService.cs`, `TaskHandler.cs`, `TimelogHandler.cs`.

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `business: APPROVED`:

- E5.A — All 12 domain-specific checks (5.1 through 5.12) passed.
- E5.B — All seven bounded-context `.csproj` projects build with zero warnings.
- E5.C — Each service's domain-level integration test (`*LifecycleIntegrationTests.cs`, `*CrudIntegrationTests.cs`) passes against LocalStack (or is documented-skip under `CognitoFactAttribute` / `RdsFactAttribute`).
- E5.D — Behavioral parity with the monolith is confirmed via a domain-expert walkthrough of each bounded context's CRUD + workflow endpoints.
- E5.E — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 5.3, 5.11):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5. Record failing checks by number (e.g., `5.3`, `5.11`) in the Findings field; set `business: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `business: APPROVED` when resolved. **Phase 6 MUST NOT begin until `business` reads `APPROVED`.**

---

## Phase 6 — Frontend Lead

> **Phase Gate:** This phase is BLOCKED from starting until frontmatter fields `devops`, `security`, `backend`, `qa`, AND `business` all read `APPROVED`. The PR is BLOCKED from merging until this phase's frontmatter field `frontend` reads `APPROVED` — and if a Phase 7 key exists, until that key also reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** Frontend Lead (SME).

**Reviewer qualifications required:** Familiarity with React 19, Vite 6 build tooling, TypeScript 5.x strict mode, Tailwind CSS 4 (utility-first), React Router 7 (data-router APIs), TanStack Query 5 (server-state management), Zustand 5 (client-state management), WCAG 2.1 accessibility basics, bundle-size budgets and code-splitting via lazy imports.

**Accountability:** The React 19 SPA, Tailwind CSS 4 styling, React Router 7 routing, TanStack Query 5 data fetching, Zustand 5 client state, the shared UI library, accessibility, bundle size, and frontend test coverage. Owns everything from the Vite build through the rendered page in the user's browser.

### B. Entry Criteria

1. Frontmatter `devops`, `security`, `backend`, `qa`, `business` all read `APPROVED`.
2. Frontmatter `frontend: OPEN`.
3. Phase 4's frontend unit/E2E test criteria (4.5, 4.9, 4.10) have already passed — the reviewer here verifies rendering + interaction, not test infrastructure.

**Reviewer action on entry:** Set frontmatter `frontend: IN_REVIEW`.

### C. Files to Review (251 files)

**Frontend root & public (3 files):**
- `apps/frontend/index.html`
- `apps/frontend/package.json`
- `apps/frontend/public/favicon.ico`

**Frontend build configuration (4 files):**
- `apps/frontend/tailwind.config.ts`
- `apps/frontend/tsconfig.app.json`
- `apps/frontend/tsconfig.json`
- `apps/frontend/vite.config.ts`

**App entry, router, top-level (4 files):**
- `apps/frontend/src/App.tsx`
- `apps/frontend/src/main.tsx`
- `apps/frontend/src/router.tsx`
- `apps/frontend/src/tailwindcss-vite.d.ts`

**API clients (14 files):**
- `apps/frontend/src/api/auth.ts`
- `apps/frontend/src/api/client.ts`
- `apps/frontend/src/api/endpoints/crm.ts`
- `apps/frontend/src/api/endpoints/entities.ts`
- `apps/frontend/src/api/endpoints/files.ts`
- `apps/frontend/src/api/endpoints/invoicing.ts`
- `apps/frontend/src/api/endpoints/notifications.ts`
- `apps/frontend/src/api/endpoints/plugins.ts`
- `apps/frontend/src/api/endpoints/projects.ts`
- `apps/frontend/src/api/endpoints/records.ts`
- `apps/frontend/src/api/endpoints/reports.ts`
- `apps/frontend/src/api/endpoints/search.ts`
- `apps/frontend/src/api/endpoints/users.ts`
- `apps/frontend/src/api/endpoints/workflows.ts`

**Page components — route subdomains (132 files):**

*`pages/admin/`* (45 files):
- `apps/frontend/src/pages/admin/AdminEntityClone.tsx`
- `apps/frontend/src/pages/admin/AdminEntityCreate.tsx`
- `apps/frontend/src/pages/admin/AdminEntityData.tsx`
- `apps/frontend/src/pages/admin/AdminEntityDataCreate.tsx`
- `apps/frontend/src/pages/admin/AdminEntityDataManage.tsx`
- `apps/frontend/src/pages/admin/AdminEntityDetails.tsx`
- `apps/frontend/src/pages/admin/AdminEntityFieldCreate.tsx`
- `apps/frontend/src/pages/admin/AdminEntityFieldDetails.tsx`
- `apps/frontend/src/pages/admin/AdminEntityFieldManage.tsx`
- `apps/frontend/src/pages/admin/AdminEntityFields.tsx`
- `apps/frontend/src/pages/admin/AdminEntityList.tsx`
- `apps/frontend/src/pages/admin/AdminEntityManage.tsx`
- `apps/frontend/src/pages/admin/AdminEntityPages.tsx`
- `apps/frontend/src/pages/admin/AdminEntityRelationCreate.tsx`
- `apps/frontend/src/pages/admin/AdminEntityRelationDetails.tsx`
- `apps/frontend/src/pages/admin/AdminEntityRelationManage.tsx`
- `apps/frontend/src/pages/admin/AdminEntityRelations.tsx`
- `apps/frontend/src/pages/admin/AdminEntityWebApi.tsx`
- `apps/frontend/src/pages/admin/AdminLayout.tsx`
- `apps/frontend/src/pages/admin/ApplicationCreate.tsx`
- `apps/frontend/src/pages/admin/ApplicationDetails.tsx`
- `apps/frontend/src/pages/admin/ApplicationList.tsx`
- `apps/frontend/src/pages/admin/ApplicationManage.tsx`
- `apps/frontend/src/pages/admin/ApplicationPages.tsx`
- `apps/frontend/src/pages/admin/ApplicationSitemap.tsx`
- `apps/frontend/src/pages/admin/CodeGenTool.tsx`
- `apps/frontend/src/pages/admin/DataSourceCreate.tsx`
- `apps/frontend/src/pages/admin/DataSourceDetails.tsx`
- `apps/frontend/src/pages/admin/DataSourceList.tsx`
- `apps/frontend/src/pages/admin/DataSourceManage.tsx`
- `apps/frontend/src/pages/admin/JobList.tsx`
- `apps/frontend/src/pages/admin/LogList.tsx`
- `apps/frontend/src/pages/admin/PageCreate.tsx`
- `apps/frontend/src/pages/admin/PageDetails.tsx`
- `apps/frontend/src/pages/admin/PageList.tsx`
- `apps/frontend/src/pages/admin/PageManage.tsx`
- `apps/frontend/src/pages/admin/RoleCreate.tsx`
- `apps/frontend/src/pages/admin/RoleDetails.tsx`
- `apps/frontend/src/pages/admin/RoleList.tsx`
- `apps/frontend/src/pages/admin/RoleManage.tsx`
- `apps/frontend/src/pages/admin/SchedulePlanList.tsx`
- `apps/frontend/src/pages/admin/UserCreate.tsx`
- `apps/frontend/src/pages/admin/UserDetails.tsx`
- `apps/frontend/src/pages/admin/UserList.tsx`
- `apps/frontend/src/pages/admin/UserManage.tsx`

*`pages/auth/`* (2 files):
- `apps/frontend/src/pages/auth/Login.tsx`
- `apps/frontend/src/pages/auth/Logout.tsx`

*`pages/crm/`* (8 files):
- `apps/frontend/src/pages/crm/AccountCreate.tsx`
- `apps/frontend/src/pages/crm/AccountDetails.tsx`
- `apps/frontend/src/pages/crm/AccountList.tsx`
- `apps/frontend/src/pages/crm/AccountManage.tsx`
- `apps/frontend/src/pages/crm/ContactCreate.tsx`
- `apps/frontend/src/pages/crm/ContactDetails.tsx`
- `apps/frontend/src/pages/crm/ContactList.tsx`
- `apps/frontend/src/pages/crm/ContactManage.tsx`

*`pages/entities/`* (12 files):
- `apps/frontend/src/pages/entities/EntityCreate.tsx`
- `apps/frontend/src/pages/entities/EntityDetails.tsx`
- `apps/frontend/src/pages/entities/EntityList.tsx`
- `apps/frontend/src/pages/entities/EntityManage.tsx`
- `apps/frontend/src/pages/entities/FieldCreate.tsx`
- `apps/frontend/src/pages/entities/FieldDetails.tsx`
- `apps/frontend/src/pages/entities/FieldList.tsx`
- `apps/frontend/src/pages/entities/FieldManage.tsx`
- `apps/frontend/src/pages/entities/RelationCreate.tsx`
- `apps/frontend/src/pages/entities/RelationDetails.tsx`
- `apps/frontend/src/pages/entities/RelationList.tsx`
- `apps/frontend/src/pages/entities/RelationManage.tsx`

*`pages/files/`* (3 files):
- `apps/frontend/src/pages/files/FileDetails.tsx`
- `apps/frontend/src/pages/files/FileList.tsx`
- `apps/frontend/src/pages/files/FileUpload.tsx`

*`pages/home/`* (4 files):
- `apps/frontend/src/pages/home/AppHome.tsx`
- `apps/frontend/src/pages/home/AppNode.tsx`
- `apps/frontend/src/pages/home/Dashboard.tsx`
- `apps/frontend/src/pages/home/SitePage.tsx`

*`pages/inventory/`* (6 files):
- `apps/frontend/src/pages/inventory/ProductCreate.tsx`
- `apps/frontend/src/pages/inventory/ProductDetails.tsx`
- `apps/frontend/src/pages/inventory/ProductList.tsx`
- `apps/frontend/src/pages/inventory/ProductManage.tsx`
- `apps/frontend/src/pages/inventory/StockAdjustment.tsx`
- `apps/frontend/src/pages/inventory/StockList.tsx`

*`pages/invoicing/`* (10 files):
- `apps/frontend/src/pages/invoicing/InvoiceCreate.tsx`
- `apps/frontend/src/pages/invoicing/InvoiceDetails.tsx`
- `apps/frontend/src/pages/invoicing/InvoiceList.tsx`
- `apps/frontend/src/pages/invoicing/InvoiceManage.tsx`
- `apps/frontend/src/pages/invoicing/PaymentCreate.tsx`
- `apps/frontend/src/pages/invoicing/PaymentDetails.tsx`
- `apps/frontend/src/pages/invoicing/PaymentList.tsx`
- `apps/frontend/src/pages/invoicing/QuoteCreate.tsx`
- `apps/frontend/src/pages/invoicing/QuoteDetails.tsx`
- `apps/frontend/src/pages/invoicing/QuoteList.tsx`

*`pages/notifications/`* (7 files):
- `apps/frontend/src/pages/notifications/EmailCompose.tsx`
- `apps/frontend/src/pages/notifications/EmailDetails.tsx`
- `apps/frontend/src/pages/notifications/EmailList.tsx`
- `apps/frontend/src/pages/notifications/NotificationCenter.tsx`
- `apps/frontend/src/pages/notifications/SmtpServiceCreate.tsx`
- `apps/frontend/src/pages/notifications/SmtpServiceList.tsx`
- `apps/frontend/src/pages/notifications/SmtpServiceManage.tsx`

*`pages/plugins/`* (3 files):
- `apps/frontend/src/pages/plugins/PluginDetails.tsx`
- `apps/frontend/src/pages/plugins/PluginList.tsx`
- `apps/frontend/src/pages/plugins/PluginManage.tsx`

*`pages/projects/`* (11 files):
- `apps/frontend/src/pages/projects/CommentList.tsx`
- `apps/frontend/src/pages/projects/FeedList.tsx`
- `apps/frontend/src/pages/projects/MonthlyTimelogReport.tsx`
- `apps/frontend/src/pages/projects/ProjectDashboard.tsx`
- `apps/frontend/src/pages/projects/TaskCreate.tsx`
- `apps/frontend/src/pages/projects/TaskDetails.tsx`
- `apps/frontend/src/pages/projects/TaskList.tsx`
- `apps/frontend/src/pages/projects/TaskManage.tsx`
- `apps/frontend/src/pages/projects/TimelogCreate.tsx`
- `apps/frontend/src/pages/projects/TimelogList.tsx`
- `apps/frontend/src/pages/projects/TimesheetView.tsx`

*`pages/records/`* (8 files):
- `apps/frontend/src/pages/records/RecordCreate.tsx`
- `apps/frontend/src/pages/records/RecordDetails.tsx`
- `apps/frontend/src/pages/records/RecordList.tsx`
- `apps/frontend/src/pages/records/RecordManage.tsx`
- `apps/frontend/src/pages/records/RecordRelatedRecordCreate.tsx`
- `apps/frontend/src/pages/records/RecordRelatedRecordDetails.tsx`
- `apps/frontend/src/pages/records/RecordRelatedRecordManage.tsx`
- `apps/frontend/src/pages/records/RecordRelatedRecordsList.tsx`

*`pages/reports/`* (5 files):
- `apps/frontend/src/pages/reports/AnalyticsOverview.tsx`
- `apps/frontend/src/pages/reports/DashboardList.tsx`
- `apps/frontend/src/pages/reports/DashboardView.tsx`
- `apps/frontend/src/pages/reports/ReportCreate.tsx`
- `apps/frontend/src/pages/reports/ReportManage.tsx`

*`pages/workflows/`* (8 files):
- `apps/frontend/src/pages/workflows/ExecutionDetails.tsx`
- `apps/frontend/src/pages/workflows/ExecutionList.tsx`
- `apps/frontend/src/pages/workflows/ScheduleList.tsx`
- `apps/frontend/src/pages/workflows/ScheduleManage.tsx`
- `apps/frontend/src/pages/workflows/WorkflowCreate.tsx`
- `apps/frontend/src/pages/workflows/WorkflowDetails.tsx`
- `apps/frontend/src/pages/workflows/WorkflowList.tsx`
- `apps/frontend/src/pages/workflows/WorkflowManage.tsx`

**Shared UI components (50 files):**

*`components/common/`* (9 files):
- `apps/frontend/src/components/common/Button.tsx`
- `apps/frontend/src/components/common/Chart.tsx`
- `apps/frontend/src/components/common/ClipboardIcons.tsx`
- `apps/frontend/src/components/common/Drawer.tsx`
- `apps/frontend/src/components/common/LoadingSpinner.tsx`
- `apps/frontend/src/components/common/Modal.tsx`
- `apps/frontend/src/components/common/PageBodyNodeRenderer.tsx`
- `apps/frontend/src/components/common/ScreenMessage.tsx`
- `apps/frontend/src/components/common/TabNav.tsx`

*`components/data-table/`* (2 files):
- `apps/frontend/src/components/data-table/DataTable.tsx`
- `apps/frontend/src/components/data-table/FilterField.tsx`

*`components/fields/`* (30 files):
- `apps/frontend/src/components/fields/AutonumberField.tsx`
- `apps/frontend/src/components/fields/CheckboxField.tsx`
- `apps/frontend/src/components/fields/CheckboxGridField.tsx`
- `apps/frontend/src/components/fields/CheckboxListField.tsx`
- `apps/frontend/src/components/fields/CodeField.tsx`
- `apps/frontend/src/components/fields/ColorField.tsx`
- `apps/frontend/src/components/fields/CurrencyField.tsx`
- `apps/frontend/src/components/fields/DataCsvField.tsx`
- `apps/frontend/src/components/fields/DateField.tsx`
- `apps/frontend/src/components/fields/DateTimeField.tsx`
- `apps/frontend/src/components/fields/EmailField.tsx`
- `apps/frontend/src/components/fields/FieldRenderer.tsx`
- `apps/frontend/src/components/fields/FileField.tsx`
- `apps/frontend/src/components/fields/GuidField.tsx`
- `apps/frontend/src/components/fields/HiddenField.tsx`
- `apps/frontend/src/components/fields/HtmlField.tsx`
- `apps/frontend/src/components/fields/IconField.tsx`
- `apps/frontend/src/components/fields/ImageField.tsx`
- `apps/frontend/src/components/fields/MultiFileUploadField.tsx`
- `apps/frontend/src/components/fields/MultiSelectField.tsx`
- `apps/frontend/src/components/fields/NumberField.tsx`
- `apps/frontend/src/components/fields/PasswordField.tsx`
- `apps/frontend/src/components/fields/PercentField.tsx`
- `apps/frontend/src/components/fields/PhoneField.tsx`
- `apps/frontend/src/components/fields/RadioListField.tsx`
- `apps/frontend/src/components/fields/SelectField.tsx`
- `apps/frontend/src/components/fields/TextField.tsx`
- `apps/frontend/src/components/fields/TextareaField.tsx`
- `apps/frontend/src/components/fields/TimeField.tsx`
- `apps/frontend/src/components/fields/UrlField.tsx`

*`components/forms/`* (3 files):
- `apps/frontend/src/components/forms/DynamicForm.tsx`
- `apps/frontend/src/components/forms/FormRow.tsx`
- `apps/frontend/src/components/forms/FormSection.tsx`

*`components/layout/`* (6 files):
- `apps/frontend/src/components/layout/AppShell.tsx`
- `apps/frontend/src/components/layout/Breadcrumb.tsx`
- `apps/frontend/src/components/layout/Header.tsx`
- `apps/frontend/src/components/layout/Sidebar.tsx`
- `apps/frontend/src/components/layout/TopNav.tsx`
- `apps/frontend/src/components/layout/UserMenu.tsx`

**Data-fetching hooks (TanStack Query) (14 files):**
- `apps/frontend/src/hooks/useApps.ts`
- `apps/frontend/src/hooks/useAuth.ts`
- `apps/frontend/src/hooks/useCrm.ts`
- `apps/frontend/src/hooks/useEntities.ts`
- `apps/frontend/src/hooks/useFiles.ts`
- `apps/frontend/src/hooks/useNotifications.ts`
- `apps/frontend/src/hooks/usePages.ts`
- `apps/frontend/src/hooks/usePlugins.ts`
- `apps/frontend/src/hooks/useProjects.ts`
- `apps/frontend/src/hooks/useRecords.ts`
- `apps/frontend/src/hooks/useReports.ts`
- `apps/frontend/src/hooks/useSearch.ts`
- `apps/frontend/src/hooks/useUsers.ts`
- `apps/frontend/src/hooks/useWorkflows.ts`

**Client state stores (Zustand) (4 files):**
- `apps/frontend/src/stores/appStore.ts`
- `apps/frontend/src/stores/authStore.ts`
- `apps/frontend/src/stores/pageBuilderStore.ts`
- `apps/frontend/src/stores/uiStore.ts`

**TypeScript type definitions (10 files):**
- `apps/frontend/src/types/app.ts`
- `apps/frontend/src/types/common.ts`
- `apps/frontend/src/types/component.ts`
- `apps/frontend/src/types/datasource.ts`
- `apps/frontend/src/types/entity.ts`
- `apps/frontend/src/types/filter.ts`
- `apps/frontend/src/types/page.ts`
- `apps/frontend/src/types/record.ts`
- `apps/frontend/src/types/theme.ts`
- `apps/frontend/src/types/user.ts`

**Utilities (5 files):**
- `apps/frontend/src/utils/constants.ts`
- `apps/frontend/src/utils/formatters.ts`
- `apps/frontend/src/utils/helpers.ts`
- `apps/frontend/src/utils/permissions.ts`
- `apps/frontend/src/utils/validators.ts`

**Shared UI library (11 files):**
- `libs/shared-ui/package.json`
- `libs/shared-ui/project.json`
- `libs/shared-ui/src/components/DataTable.tsx`
- `libs/shared-ui/src/components/FieldComponents.tsx`
- `libs/shared-ui/src/components/Form.tsx`
- `libs/shared-ui/src/hooks/useApi.ts`
- `libs/shared-ui/src/hooks/useAuth.ts`
- `libs/shared-ui/src/hooks/usePagination.ts`
- `libs/shared-ui/src/index.ts`
- `libs/shared-ui/src/types/index.ts`
- `libs/shared-ui/tsconfig.json`

_Total: 251 files._

### D. Domain-Specific Checks

- **6.1** Production Vite build succeeds with zero errors — `cd apps/frontend && npx vite build` produces a clean `dist/` directory without TypeScript errors, failed imports, or missing asset warnings.
- **6.2** Bundle size is within documented thresholds — per-route chunk size < 200 KB gzipped (AAP §0.8.2). Verify with `npx vite build --mode production` and inspect the build output or `rollup-plugin-visualizer` report; main entry bundle must stay small and route-level pages must be code-split via React Router lazy imports.
- **6.3** No hardcoded URLs in frontend code — all API base URLs are read from `VITE_API_URL` environment variable via `apps/frontend/src/api/client.ts`; zero occurrences of literal `http://localhost:4566`, `http://localhost:3000`, or production URLs in `.tsx`/`.ts` files outside `.env*` configuration and `vite.config.ts`.
- **6.4** Accessible labels on all interactive elements — every `<button>`, `<input>`, `<select>`, `<textarea>`, `<a>` with an icon-only child has an `aria-label`, `aria-labelledby`, or visible text label. All field components in `apps/frontend/src/components/fields/` and `libs/shared-ui/src/components/` render `<label htmlFor="…">` bound to the input's `id`. `FieldRenderer.tsx` propagates the label prop to every concrete field type.
- **6.5** Error states handled in UI — each TanStack Query hook (`apps/frontend/src/hooks/useAuth.ts`, `useCrm.ts`, `useEntities.ts`, `useFiles.ts`, `useNotifications.ts`, `usePages.ts`, `usePlugins.ts`, `useProjects.ts`, `useRecords.ts`, `useReports.ts`, `useSearch.ts`, `useUsers.ts`, `useWorkflows.ts`, `useApps.ts`) exposes an `error` state that is rendered in the consuming page via `ScreenMessage.tsx` or an inline error panel; no swallowed exceptions; no silent failures.
- **6.6** New components are tested — every new `*.tsx` file under `apps/frontend/src/components/` and `apps/frontend/src/pages/` has a corresponding Vitest spec under `apps/frontend/tests/unit/` OR is covered by a Playwright E2E test under `apps/frontend-e2e/src/` OR `apps/frontend/tests/e2e/`. Field components in `apps/frontend/src/components/fields/` must each have a rendering/interaction test.
- **6.7** No orphaned exports — every named export in `apps/frontend/src/**/*.ts[x]` and `libs/shared-ui/src/**/*.ts[x]` is referenced somewhere; dead code from the StencilJS/jQuery prior art is removed, not kept for "future reference". Use `npx nx run frontend:lint` with `@typescript-eslint/no-unused-vars` and `import/no-unused-modules` rules enabled.
- **6.8** Router configuration is complete and consistent — `apps/frontend/src/router.tsx` registers every page component under `apps/frontend/src/pages/**` with a stable path; protected routes enforce authentication via the Cognito session read from `authStore.ts`; 404 route present.
- **6.9** API client wiring is correct — `apps/frontend/src/api/client.ts` injects `Authorization: Bearer <idToken>` from `authStore.ts` on every request; request/response interceptors surface 401s as auth-logout events; every endpoint module under `apps/frontend/src/api/endpoints/` uses the shared `client` not raw `fetch`/`axios`.
- **6.10** Tailwind configuration aligns with the design system — `apps/frontend/tailwind.config.ts` defines the palette, spacing scale, and typography matching the intended theme; the Bootstrap 4 monolith theme is fully replaced (AAP §0.7.7) and no residual Bootstrap classes remain in JSX.
- **6.11** TypeScript strict mode — `apps/frontend/tsconfig.json` and `apps/frontend/tsconfig.app.json` enable `strict: true`, `noImplicitAny: true`, `strictNullChecks: true`; shared `tsconfig.base.json` is extended correctly with path aliases for `libs/*`.
- **6.12** Client state discipline — Zustand stores (`apps/frontend/src/stores/authStore.ts`, `appStore.ts`, `pageBuilderStore.ts`, `uiStore.ts`) hold only client-only concerns (UI state, auth tokens); server-fetched data flows through TanStack Query caches, not stores; no duplication of remote state into stores.
- **6.13** Shared UI library boundaries — `libs/shared-ui` exports only reusable primitives (`DataTable`, `FieldComponents`, `Form`, `useApi`, `useAuth`, `usePagination`); it does not import from `apps/frontend`; `apps/frontend` consumes the library via the path alias declared in `tsconfig.base.json`.

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets frontmatter `frontend: APPROVED`:

- E6.A — All 13 domain-specific checks (6.1 through 6.13) passed.
- E6.B — `cd apps/frontend && npx vite build` exits 0.
- E6.C — `cd apps/frontend && npx vitest run` passes 100% with zero failures and zero skipped tests.
- E6.D — All new pages and components have tests (per 6.6).
- E6.E — Bundle size is within thresholds (per 6.2).
- E6.F — Accessibility checks pass on all interactive elements (per 6.4).
- E6.G — Router is complete (per 6.8), and no orphaned exports remain (per 6.7).
- E6.H — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 6.1, 6.4):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5. Record failing checks by number (e.g., `6.1`, `6.4`) in the Findings field; set `frontend: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `frontend: APPROVED` when resolved. **If no Phase 7 is activated, on APPROVED, set top-level `status: APPROVED` and the PR becomes merge-eligible.**

---

## Phase 7 — Other SMEs (Conditional / Template)

> **Activation:** This phase is OPTIONAL and is activated only when the Segmented PR Review Rule R6 escalation applies (e.g., data engineering, ML ops, compliance, accessibility-specialist, licensing, internationalization concerns surfaced during earlier phases). If activated, add the corresponding key(s) to the frontmatter (e.g., `accessibility: OPEN`) and clone the template below for each additional SME track. Phase 7 executes AFTER Phase 6 reads `APPROVED`. Multiple Phase 7 tracks — when required — run sequentially (7a → 7b → …), not in parallel.

> **Phase Gate (when activated):** BLOCKED from starting until frontmatter fields `devops`, `security`, `backend`, `qa`, `business`, AND `frontend` all read `APPROVED`. The PR is BLOCKED from merging until every activated Phase 7 key reads `APPROVED`.

### A. Reviewer Role

**Reviewer title:** `<SME role label — e.g., Data Engineer | ML Ops | Compliance Officer | Accessibility Specialist | Localization Lead>`.

**Reviewer qualifications required:** `<Specific domain knowledge required for this SME track.>`

**Accountability:** `<Specific area of the codebase or cross-cutting concern owned by this SME.>`

### B. Entry Criteria

1. Frontmatter `devops`, `security`, `backend`, `qa`, `business`, `frontend` all read `APPROVED`.
2. Frontmatter `<this-sme-key>: OPEN`.
3. The activating reviewer (from any earlier phase) has documented in the PR description the reason this Phase 7 track is required and has identified the SME owner.

**Reviewer action on entry:** Set frontmatter `<this-sme-key>: IN_REVIEW`.

### C. Files to Review

`<Enumerate the specific files in this SME's scope.>`

### D. Domain-Specific Checks

- **7.1** `<SME-specific check.>`
- **7.2** `<SME-specific check.>`
- **7.n** `<SME-specific check.>`

### E. Exit Criteria

All of the following MUST evaluate to true before the reviewer sets `<this-sme-key>: APPROVED`:

- E7.A — All SME-specific checks (7.1 through 7.n) passed.
- E7.B — `<SME-specific objective gate, e.g., `axe-core` reports zero WCAG 2.1 AA violations.>`
- E7.C — The reviewer has recorded their name and date in the Sign-Off Block below.

### F. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED, list by unique check number, e.g., 7.1):
______________
______________
______________

### G. FAIL STATE Protocol

Follow R5. Record failing checks by number; set `<this-sme-key>: BLOCKED` and `status: BLOCKED`; PR author remediates; reviewer re-reviews only changed files; set `<this-sme-key>: APPROVED` when resolved. **The PR remains BLOCKED from merging until every activated Phase 7 key reads `APPROVED`.**

---

## Final Merge Gate

The PR is merge-eligible only when ALL of the following are simultaneously true:

1. Every enumerated phase field in the YAML frontmatter reads `APPROVED`:
   - `devops: APPROVED`
   - `security: APPROVED`
   - `backend: APPROVED`
   - `qa: APPROVED`
   - `business: APPROVED`
   - `frontend: APPROVED`
   - (and every activated Phase 7 key, e.g., `accessibility: APPROVED`).
2. The top-level `status:` frontmatter field reads `APPROVED`.
3. No phase field reads `BLOCKED`.
4. The three Gate Verification Commands in R8 return the expected outputs.
5. All sign-off blocks have recorded reviewer names and dates.

If any of these conditions is false, the PR MUST NOT be merged, regardless of the number of approving reviews the PR otherwise has. Partial sign-off is not approval (R7).

---
