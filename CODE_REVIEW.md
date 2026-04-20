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

# Code Review — Nx Monorepo Serverless Migration (PR `blitzy-28124201-2161-4a8d-a225-5250ade8f419`)

**PR scope summary:** Complete architectural rewrite of the WebVella ERP monolith (ASP.NET Core 9 + Razor Pages + PostgreSQL) into a serverless Nx monorepo comprising a React 19 SPA, 10 .NET 9 Native AOT Lambda services, a Node.js 22 Lambda authorizer, 4 shared libraries, AWS CDK infrastructure, and LocalStack-based integration tests (748 added/modified files).

All six phases MUST be completed and signed off IN ORDER. No phase may begin until the preceding phase status is APPROVED. Partial sign-off does not constitute approval.

**Frontmatter contract:** The YAML block at the top of this file is the sole authoritative source of review status. Valid values per phase field: `OPEN` | `IN_REVIEW` | `BLOCKED` | `APPROVED`. Phase N+1 field MUST remain `OPEN` until Phase N field reads `APPROVED`.

---

## Phase 1 — DevOps Engineer

### A. Reviewer Role

DevOps Engineer — accountable for CI/CD integrity, Infrastructure-as-Code correctness, container and monorepo configuration, and deployment/bootstrap scripts. Owns the pipeline from source to running environment.

### B. Files to Review

106 files in scope for this phase.

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
- `blitzy/documentation/Project`
- `blitzy/documentation/Technical`
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

### C. Domain-Specific Checks

1. CI/CD step ordering — tests run before deploy; no deploy job depends on a skipped/failing test job.
2. Image versions pinned in `docker-compose.yml` and workflow files — no `:latest` tags for LocalStack or any other service image.
3. No plaintext secrets in any config file (`nx.json`, `package.json`, `docker-compose.yml`, `.github/workflows/*.yml`, `infra/cdk.json`, `infra/cdk.context.json`).
4. IaC validates without errors — `cd infra && npx tsc --noEmit` returns 0; `npx cdk synth --context localstack=true` produces a valid template without errors.
5. Build scripts idempotent — `tools/scripts/bootstrap-localstack.sh`, `run-migrations.sh`, and `seed-test-data.sh` can be re-executed safely without corrupting existing state.
6. Required environment variables documented in `README.md` (`AWS_ENDPOINT_URL`, `AWS_REGION`, `COGNITO_USER_POOL_ID`, `API_GATEWAY_URL`, `IS_LOCAL`, `VITE_API_URL`, `LOCALSTACK_AUTH_TOKEN`).
7. Nx workspace: `nx.json` defines task pipelines (`build`, `test`, `lint`, `e2e`) with caching; `project.json` files exist for every app, service, and lib.
8. `.gitignore` / `.blitzyignore` cover `node_modules/`, `.localstack/`, `volume/`, `localstack/`, `cdk.out/`, `*.env`, `.env.*`, `dist/`, `build/`, `coverage/`, `*.tfstate`.
9. CDK dual-target via `localstack` context flag — LocalStack-only resources (RDS stub, JWT authorizer fallback) are conditional, production-only resources (CloudFront, Route 53, ACM) are conditional.
10. GitHub Actions workflows reference the `localstack/setup-localstack` action for LocalStack-backed CI runs.

### D. Sign-Off Criteria

Phase APPROVED when: all 10 domain-specific checks pass, `npx tsc --noEmit` succeeds in `infra/` and `libs/shared-cdk-constructs/`, `docker-compose config` validates, and `.github/workflows/*.yml` pass YAML lint with documented LocalStack integration. Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---

## Phase 2 — Security Expert

### A. Reviewer Role

Security Expert — accountable for authentication, authorization, token validation, IAM policies, secrets management, and the attack surface of identity-related Lambda handlers and the JWT authorizer.

### B. Files to Review

17 files in scope for this phase.

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

### C. Domain-Specific Checks

1. No `alg:none` path — JWT verifier rejects tokens with algorithm `none`; only `RS256` (Cognito) and `HS256` (LocalStack dev-only) accepted.
2. Token expiry validated — `exp` claim checked with clock-skew tolerance; expired tokens rejected.
3. Deny-by-default authorization — authorizer returns `Deny` policy unless a valid JWT is proven; missing or malformed Authorization header rejected.
4. No wildcard IAM/RBAC grants — `PermissionService.cs` enumerates explicit permissions per role; no `Resource: '*'` or `Action: '*'` grants except for Lambda logs.
5. Secrets sourced from SSM Parameter Store `SecureString`, not environment variables; `DB_CONNECTION_STRING` and `COGNITO_CLIENT_SECRET` never appear in plaintext.
6. Unauthenticated routes enumerated and justified — only `/health` and `/v1/auth/login` bypass the authorizer; list explicitly declared in `infra/src/stacks/api-gateway-stack.ts`.
7. No sensitive fields in response schemas — password hashes, refresh tokens, and session IDs are never returned by `UserHandler`, `RoleHandler`, or `AuthHandler`.
8. Parameterized queries only — `UserRepository.cs` uses DynamoDB SDK with attribute values; zero string interpolation into DynamoDB expressions or SQL.
9. User-migration trigger (`services/identity/src/triggers/user-migration/index.js`) validates legacy MD5 hash with constant-time comparison (no timing oracle) and re-issues credentials through Cognito's secure hashing.
10. `jwt-validator.ts` uses `jwks-rsa` with cache + rate limit; JWKS key rotation supported without service restart.
11. CORS on identity endpoints locked to the frontend's documented origins (no `*`).
12. Input validation on `AuthHandler.Login` (email format, password length) and on `RoleHandler` / `UserHandler` POST/PUT bodies.

### D. Sign-Off Criteria

Phase APPROVED when: all 12 domain-specific checks pass, `dotnet build services/identity/Identity.csproj` succeeds with zero warnings, `npm run build` in `services/authorizer` succeeds, unit tests for `jwt-validator` and `CognitoService` achieve ≥ 80% branch coverage, and no HIGH/CRITICAL findings remain from dependency audit (`npm audit --audit-level=high`). Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---

## Phase 3 — Backend Lead

### A. Reviewer Role

Backend Lead — accountable for service boundaries, handler/business-logic/data-access layering, event schema correctness, API contract conformance, shared cross-service utilities, migration ordering, and correlation-ID propagation across services.

### B. Files to Review

86 files in scope for this phase.

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

### C. Domain-Specific Checks

1. No cross-service internal imports — no `services/X/src/**` file imports from `services/Y/src/**`. Cross-service communication must flow through published OpenAPI endpoints or JSON Schema events only.
2. Handlers (`services/*/src/Functions/*Handler.cs`) contain zero business logic — handlers deserialize input, call a service method, serialize output, and handle errors. Business rules belong in `Services/`.
3. Repositories (`services/*/src/DataAccess/*Repository.cs`) contain zero business logic — pure persistence operations (get, put, query, delete, update); validation and rule evaluation occur in `Services/`.
4. Event payloads published by any service match the corresponding JSON Schema in `libs/shared-schemas/src/events/*.json`; every emitted SNS event validates against its schema.
5. Migrations ordered and idempotent — `services/invoicing/src/Migrations/InitialCreate.cs` and `services/reporting/src/Migrations/Migration_001_InitialSchema.cs` use FluentMigrator with explicit version numbers and re-runnable statements.
6. No hardcoded resource IDs or connection strings — all table names, bucket names, queue ARNs, topic ARNs come from environment variables or SSM parameters sourced in `Program.cs`.
7. Correlation IDs propagated — every outbound call (SNS publish, SQS send, HTTP invoke) includes `X-Correlation-Id` from the incoming Lambda event context via `libs/shared-utils/src/correlation-id.ts`.
8. OpenAPI specs in `libs/shared-schemas/src/api/*.yaml` match the actual routes declared in API Gateway stack and implemented in Lambda handlers; contract drift detected via contract tests.
9. Shared utilities (`libs/shared-utils`) are pure and free of service-specific logic — `logger`, `correlation-id`, and `idempotency` modules exported without bleed of domain types.
10. Entity Management owns all entity/field/relation metadata — no other service reads or writes the entity-metadata DynamoDB table; other services call Entity Management's API.
11. Plugin System owns plugin registry — plugin metadata persisted in a dedicated DynamoDB table; no cross-service access to plugin data.
12. 20+ field type classes in `services/entity-management/src/Models/FieldTypes/` preserve behavioral parity with the monolith (`WebVella.Erp/Database/FieldTypes/`).

### D. Sign-Off Criteria

Phase APPROVED when: all 12 domain-specific checks pass, `dotnet build services/entity-management/EntityManagement.csproj` and `dotnet build services/plugin-system/PluginSystem.csproj` succeed with zero warnings, `npx tsc --noEmit` succeeds in `libs/shared-schemas` and `libs/shared-utils`, all 10 OpenAPI specs validate (`npx @redocly/cli lint libs/shared-schemas/src/api/*.yaml`), and all 10 JSON Schema event documents parse as valid JSON Schema. Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---
## Phase 4 — QA Engineer

### A. Reviewer Role

QA Engineer — accountable for test completeness, test correctness, reliability against LocalStack, cross-test isolation, fixture cleanliness, and coverage of new code paths and critical user journeys introduced by this PR.

### B. Files to Review

190 files in scope for this phase.


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

**Service test project files (.csproj) (11 files):**
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
- `services/workflow/tests/WorkflowTests.csproj`

**Test configuration (6 files):**
- `apps/frontend/tsconfig.spec.json`
- `apps/frontend/vitest.config.ts`
- `libs/shared-schemas/vitest.config.ts`
- `libs/shared-ui/vitest.config.ts`
- `services/authorizer/vitest.config.ts`
- `services/entity-management/tests/xunit.runner.json`

_Total: 190 files classified in QA phase._

### C. Domain-Specific Checks

1. Zero failures and zero skipped tests with CI artifact attached — test run produces a machine-readable report (TRX for .NET, JUnit XML for Vitest/Playwright) uploaded as a CI workflow artifact.
2. Integration tests hit real dependencies (zero mock/stub matches for infrastructure) — grep for `Mock<IAmazonDynamoDB>`, `Mock<IAmazonS3>`, etc. in `tests/Integration/` returns zero results; real AWS SDK clients instantiated against LocalStack endpoint.
3. Teardown prevents cross-test state leakage — every `LocalStackFixture.cs` / `DatabaseFixture.cs` implements `IDisposable` or `IAsyncLifetime.DisposeAsync` that cleans up DynamoDB items, S3 objects, SQS queues, SNS topics, Cognito users, and RDS schemas created during tests.
4. New code paths have coverage — every handler, service method, and repository method added to the services covered in Phases 2, 3, and 5 has at least one unit test and, for integration-critical paths, at least one integration test.
5. E2E covers critical journeys introduced by PR — login, record CRUD, CRM contact creation, project task workflow, notifications, admin console, navigation — all have Playwright specs in `apps/frontend-e2e/src/` or `apps/frontend/tests/e2e/`.
6. Test project files (`*.Tests.csproj`) reference correct SDK `Microsoft.NET.Sdk` with `IsPackable=false` and target `net9.0`; both `services/workflow/tests/Workflow.Tests.csproj` and `services/workflow/tests/WorkflowTests.csproj` are disambiguated (per setup-status note, `WorkflowTests.csproj` is authoritative; `dotnet test` must explicitly specify a project file).
7. Integration tests that require LocalStack Pro services (`cognito-idp`, `rds`) are attributed with `CognitoFactAttribute` / `RdsFactAttribute` so they are skipped (not failed) when the Pro licence is unavailable; tests document the skip reason.
8. Test fixtures seed deterministic test data — no reliance on unspecified order; tests run correctly under parallel execution or are serialized via xUnit collection fixtures (`IntegrationCollection`).
9. Frontend unit tests render components with `@testing-library/react` and assert on user-facing behavior, not implementation details. All 61 field/layout/hook/store/util tests pass under Vitest run.
10. `vitest.config.ts` in each project uses `nxViteTsPaths()` plugin or equivalent path resolution so shared library imports resolve during tests.
11. No test contains a commented-out assertion, hardcoded production URL, or real AWS credentials.
12. Contract tests (e.g., `services/crm/tests/ContractTests.cs`) validate request/response schemas against the corresponding OpenAPI definition in `libs/shared-schemas/src/api/`.

### D. Sign-Off Criteria

Phase APPROVED when: all 12 domain-specific checks pass, `dotnet test` returns `Passed!` for every service test project (unit + integration that do not require Pro services), `npx vitest run` returns 0 failures for `apps/frontend`, `libs/shared-ui`, `libs/shared-schemas`, and `services/authorizer`, `npx playwright test --project=chromium --list` enumerates all declared E2E specs without error, and new-code coverage ≥ 80% (line) for each modified service per `dotnet test /p:CollectCoverage=true`. Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---

## Phase 5 — Business / Domain Expert

### A. Reviewer Role

Business / Domain Expert — accountable for domain correctness of the seven bounded-context services (CRM, Inventory/Project Management, Invoicing, Reporting, Notifications, File Management, Workflow), acceptance-criteria coverage, backward compatibility of model changes, and alignment of implemented workflows with the monolith's behavioral parity requirement.

### B. Files to Review

98 files in scope for this phase.


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

### C. Domain-Specific Checks

1. Workflows match PR acceptance criteria — CRM account/contact CRUD, project task creation and timelog recording, invoice creation with line items and tax, payment processing, email queue processor, S3 upload/download with presigned URLs, workflow state machine execution — all implemented end-to-end.
2. Model changes backward-compatible or migration documented — per-entity field shapes in `Account.cs`, `Contact.cs`, `Invoice.cs`, `Payment.cs`, `Email.cs`, `Task.cs`, `Timelog.cs`, `FileMetadata.cs` preserve the monolith's JSON schema or document a migration path.
3. Business rules match spec — `InvoiceService`, `LineItemCalculationService`, `TaxCalculationService`, `PaymentService` implement the monolith's invoicing rules (VAT, rounding, currency handling, totals).
4. UI flows match designs — page-level React components in `apps/frontend/src/pages/crm`, `/projects`, `/invoicing`, `/notifications`, `/files`, `/workflows`, `/reports` invoke the correct domain endpoints with correct payloads.
5. Deprecated features flagged for sign-off — any monolith feature intentionally omitted (e.g., Bulgarian FTS per AAP §0.3.2) is explicitly documented here and requires domain-owner approval.
6. CRM service owns account, contact, and address entities exclusively; no other service reads or writes these tables. CRM `SearchService.cs` regenerates `x_search` token fields on create/update.
7. Invoicing service uses RDS PostgreSQL with FluentMigrator migrations (`services/invoicing/src/Migrations/InitialCreate.cs`); transaction boundaries preserve invoice/line-item/payment ACID invariants.
8. Reporting service consumes domain events via SQS and projects them into RDS PostgreSQL read models (`ProjectionService.cs`, `EventConsumer.cs`); migrations ordered (`Migration_001_InitialSchema.cs`).
9. Notifications service preserves SMTP queue semantics (`SmtpService.cs`, `QueueProcessor.cs`) — retry with backoff, priority ordering, and at-least-once delivery.
10. File Management uses S3 for storage and DynamoDB for metadata; presigned URLs have appropriate TTLs; content-type and size limits enforced.
11. Workflow engine uses Step Functions state-machine definitions (`approval-chain.json`, `daily-schedule.json`, `interval-schedule.json`, `monthly-schedule.json`, `weekly-schedule.json`) that replicate the monolith's `SheduleManager` recurrence semantics.
12. Inventory service (project management) preserves the monolith's task/timelog/comment/feed semantics with `TaskService.cs`, `TaskHandler.cs`, `TimelogHandler.cs`.

### D. Sign-Off Criteria

Phase APPROVED when: all 12 domain-specific checks pass, all seven service .csproj projects build with zero warnings, each service's domain-level integration test (`*LifecycleIntegrationTests.cs`, `*CrudIntegrationTests.cs`) passes against LocalStack, and behavioral parity with the monolith is confirmed via a domain-expert walkthrough of each bounded context's CRUD + workflow endpoints. Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---


## Phase 6 — Frontend Lead

### A. Reviewer Role

Frontend Lead — accountable for the React 19 SPA (Vite 6 + TypeScript 5.x), Tailwind CSS 4 styling, React Router 7 routing, TanStack Query 5 data fetching, Zustand 5 client state, the shared UI library, accessibility, bundle size, and frontend test coverage. Owns everything from the Vite build through the rendered page in the user's browser.

### B. Files to Review

251 files in scope for this phase.

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

### C. Domain-Specific Checks

1. Production Vite build succeeds with zero errors — `cd apps/frontend && npx vite build` produces a clean `dist/` directory without TypeScript errors, failed imports, or missing asset warnings.
2. Bundle size is within documented thresholds — per-route chunk size < 200 KB gzipped (AAP §0.8.2). Verify with `npx vite build --mode production` and inspect the build output or `rollup-plugin-visualizer` report; main entry bundle must stay small and route-level pages must be code-split via React Router lazy imports.
3. No hardcoded URLs in frontend code — all API base URLs are read from `VITE_API_URL` environment variable via `apps/frontend/src/api/client.ts`; zero occurrences of literal `http://localhost:4566`, `http://localhost:3000`, or production URLs in `.tsx`/`.ts` files outside `.env*` configuration and `vite.config.ts`.
4. Accessible labels on all interactive elements — every `<button>`, `<input>`, `<select>`, `<textarea>`, `<a>` with an icon-only child has an `aria-label`, `aria-labelledby`, or visible text label. All field components in `apps/frontend/src/components/fields/` and `libs/shared-ui/src/components/` render `<label htmlFor="…">` bound to the input's `id`. `FieldRenderer.tsx` propagates the label prop to every concrete field type.
5. Error states handled in UI — each TanStack Query hook (`apps/frontend/src/hooks/useAuth.ts`, `useCrm.ts`, `useEntities.ts`, `useFiles.ts`, `useNotifications.ts`, `usePages.ts`, `usePlugins.ts`, `useProjects.ts`, `useRecords.ts`, `useReports.ts`, `useSearch.ts`, `useUsers.ts`, `useWorkflows.ts`, `useApps.ts`) exposes an `error` state that is rendered in the consuming page via `ScreenMessage.tsx` or an inline error panel; no swallowed exceptions; no silent failures.
6. New components are tested — every new `*.tsx` file under `apps/frontend/src/components/` and `apps/frontend/src/pages/` has a corresponding Vitest spec under `apps/frontend/tests/unit/` OR is covered by a Playwright E2E test under `apps/frontend-e2e/src/` OR `apps/frontend/tests/e2e/`. Field components in `apps/frontend/src/components/fields/` must each have a rendering/interaction test.
7. No orphaned exports — every named export in `apps/frontend/src/**/*.ts[x]` and `libs/shared-ui/src/**/*.ts[x]` is referenced somewhere; dead code from the StencilJS/jQuery prior art is removed, not kept for "future reference". Use `npx nx run frontend:lint` with `@typescript-eslint/no-unused-vars` and `import/no-unused-modules` rules enabled.
8. Router configuration is complete and consistent — `apps/frontend/src/router.tsx` registers every page component under `apps/frontend/src/pages/**` with a stable path; protected routes enforce authentication via the Cognito session read from `authStore.ts`; 404 route present.
9. API client wiring is correct — `apps/frontend/src/api/client.ts` injects `Authorization: Bearer <idToken>` from `authStore.ts` on every request; request/response interceptors surface 401s as auth-logout events; every endpoint module under `apps/frontend/src/api/endpoints/` uses the shared `client` not raw `fetch`/`axios`.
10. Tailwind configuration aligns with the design system — `apps/frontend/tailwind.config.ts` defines the palette, spacing scale, and typography matching the intended theme; the Bootstrap 4 monolith theme is fully replaced (AAP §0.7.7) and no residual Bootstrap classes remain in JSX.
11. TypeScript strict mode — `apps/frontend/tsconfig.json` and `apps/frontend/tsconfig.app.json` enable `strict: true`, `noImplicitAny: true`, `strictNullChecks: true`; shared `tsconfig.base.json` is extended correctly with path aliases for `libs/*`.
12. Client state discipline — Zustand stores (`apps/frontend/src/stores/authStore.ts`, `appStore.ts`, `pageBuilderStore.ts`, `uiStore.ts`) hold only client-only concerns (UI state, auth tokens); server-fetched data flows through TanStack Query caches, not stores; no duplication of remote state into stores.
13. Shared UI library boundaries — `libs/shared-ui` exports only reusable primitives (`DataTable`, `FieldComponents`, `Form`, `useApi`, `useAuth`, `usePagination`); it does not import from `apps/frontend`; `apps/frontend` consumes the library via the path alias declared in `tsconfig.base.json`.

### D. Sign-Off Criteria

Phase APPROVED when: all 13 domain-specific checks pass, `cd apps/frontend && npx vite build` exits 0, `cd apps/frontend && npx vitest run` passes 100% with zero failures and zero skipped tests, all new pages and components have tests, bundle size is within thresholds, accessibility checks pass on all interactive elements, router is complete, and no orphaned exports remain. Reviewer records name and date below.

### E. Sign-Off Block

SIGN-OFF
Reviewer name: ______________
Date: ______________
Phase status (circle one): APPROVED / BLOCKED
Findings (if BLOCKED): ______________

FAIL STATE — if this phase is BLOCKED:

1. Reviewer documents all failing checks in the Findings field above.
2. Reviewer updates this phase's frontmatter status field to BLOCKED.
3. Reviewer notifies the PR author with a direct link to the failing checks.
4. PR author addresses all findings via new commits (no force-pushes to the review branch).
5. Reviewer re-reviews ONLY the files changed since BLOCKED status was set and updates status to APPROVED in the frontmatter when all sign-off criteria pass.
Phase N+1 MUST NOT begin until this phase's frontmatter field reads APPROVED.

---
