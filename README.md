# WebVella ERP

A serverless microservices ERP platform built with AWS Lambda, React 19, and CDK 2.x, developed and tested exclusively against LocalStack.

## Architecture Overview

WebVella ERP is structured as an **Nx monorepo** containing 10 bounded-context Lambda-backed services, a React single-page application, shared libraries, and CDK infrastructure — all deployable to both LocalStack (for local development and testing) and production AWS with a single codebase.

| Component | Technology |
|-----------|-----------|
| **Backend Services** | .NET 9 Native AOT Lambda handlers (10 services) |
| **Custom Authorizer** | Node.js 22 Lambda JWT authorizer |
| **Frontend** | React 19 SPA with Vite 6, served from S3 |
| **API Gateway** | HTTP API Gateway v2 (path-based routing to Lambda) |
| **Default Datastore** | DynamoDB (single-table design per service) |
| **ACID Datastore** | RDS PostgreSQL (Invoicing, Reporting) |
| **Messaging** | SNS topics for domain events, SQS queues for consumers |
| **Authentication** | AWS Cognito user pools with JWT authorizer |
| **Workflow Orchestration** | Step Functions Local |
| **File Storage** | S3 |
| **Configuration** | SSM Parameter Store |
| **Infrastructure as Code** | CDK 2.x (dual-target: LocalStack + AWS) |
| **CSS Framework** | Tailwind CSS 4.x |
| **Server State** | TanStack Query 5 |
| **Client State** | Zustand 5 |
| **Routing** | React Router 7 |

## Monorepo Structure

```
webvella-erp/
├── apps/
│   ├── frontend/                 React 19 SPA (Vite 6, Tailwind CSS 4)
│   └── frontend-e2e/             Playwright E2E tests
│
├── services/
│   ├── identity/                 Identity & Access Management (.NET 9)
│   ├── entity-management/        Core Entity Engine (.NET 9)
│   ├── crm/                      CRM / Contacts (.NET 9)
│   ├── inventory/                Inventory & Project Management (.NET 9)
│   ├── invoicing/                Invoicing & Billing (.NET 9, RDS PostgreSQL)
│   ├── reporting/                Reporting & Analytics (.NET 9, RDS PostgreSQL)
│   ├── notifications/            Notifications & Email (.NET 9)
│   ├── file-management/          File Management via S3 (.NET 9)
│   ├── workflow/                 Workflow Engine via Step Functions (.NET 9)
│   ├── plugin-system/            Plugin / Extension System (.NET 9)
│   └── authorizer/               Custom JWT Authorizer (Node.js 22)
│
├── libs/
│   ├── shared-schemas/           Event & API contract definitions (JSON Schema, OpenAPI)
│   ├── shared-cdk-constructs/    Reusable CDK patterns (Lambda, DynamoDB, SNS/SQS)
│   ├── shared-ui/                React component library (fields, forms, data table)
│   └── shared-utils/             Cross-service utilities (logger, correlation-id, idempotency)
│
├── infra/                        CDK 2.x stacks (one per service + shared + API Gateway)
│
├── tools/
│   ├── scripts/                  Bootstrap, seed, and migration scripts
│   └── generators/               Nx custom generators
│
├── nx.json                       Nx workspace configuration
├── package.json                  Root dependencies and workspace scripts
├── tsconfig.base.json            Base TypeScript config with library path aliases
├── docker-compose.yml            LocalStack Pro + Step Functions Local sidecar
└── .github/workflows/            CI/CD pipelines (GitHub Actions + LocalStack)
```

## Bounded-Context Services

| Service | Domain | Datastore | Description |
|---------|--------|-----------|-------------|
| **identity** | Identity & Access | Cognito + DynamoDB | User/role management, authentication via Cognito |
| **entity-management** | Core Engine | DynamoDB | Entity/field/relation metadata, record CRUD, EQL query adapter |
| **crm** | CRM / Contacts | DynamoDB | Account, contact, and address management |
| **inventory** | Projects & Inventory | DynamoDB | Task, timelog, and product management |
| **invoicing** | Billing | RDS PostgreSQL | Invoice and payment processing with ACID transactions |
| **reporting** | Analytics | RDS PostgreSQL | Event-sourced read-model projections and report generation |
| **notifications** | Messaging | DynamoDB | Email (SES-stubbed), in-app notifications, webhooks |
| **file-management** | Documents | DynamoDB + S3 | File upload/download via S3 presigned URLs |
| **workflow** | Orchestration | DynamoDB | Step Functions-backed workflow and job execution |
| **plugin-system** | Extensions | DynamoDB | Plugin registration, lifecycle, and configuration |
| **authorizer** | Security | — | Node.js JWT validation for API Gateway (LocalStack fallback) |

## Prerequisites

Before getting started, ensure the following are installed:

- **Node.js** 22 LTS
- **npm** (included with Node.js)
- **.NET SDK** 9.0
- **Docker** (for running LocalStack)
- **cdklocal** — install via `npm install -g aws-cdk-local aws-cdk`
- **awslocal** — install via `pip install awscli-local`

## Getting Started

```bash
# 1. Clone the repository and install dependencies
git clone <repository-url>
cd webvella-erp
npm install

# 2. Start LocalStack and Step Functions Local
docker compose up -d

# 3. Wait for LocalStack to be healthy
docker compose exec localstack curl -sf http://localhost:4566/_localstack/health

# 4. Bootstrap CDK against LocalStack
cdklocal bootstrap --context localstack=true

# 5. Deploy all CDK stacks to LocalStack
cdklocal deploy --all --context localstack=true --require-approval never

# 6. Run database migrations (Invoicing and Reporting RDS PostgreSQL)
./tools/scripts/run-migrations.sh

# 7. Seed test data (Cognito users, sample records)
./tools/scripts/seed-test-data.sh

# 8. Start the frontend development server
npx nx serve frontend

# 9. Run all tests
npx nx run-many --target=test --all
```

> **Note:** The default Cognito test user is `erp@webvella.com` with password `erp`.

## Development

### Common Commands

```bash
# Build a specific service
npx nx build <project-name>

# Run tests for a specific service
npx nx test <project-name>

# Run E2E tests
npx nx e2e frontend-e2e

# Lint a project
npx nx lint <project-name>

# Run all builds across the monorepo
npx nx run-many --target=build --all

# Run all tests across the monorepo
npx nx run-many --target=test --all

# Run only affected projects (based on git diff)
npx nx affected --target=test
npx nx affected --target=build
```

### .NET Service Development

Each .NET service under `services/` is an independent Lambda project:

```bash
# Build a .NET service
dotnet build services/identity/Identity.csproj

# Run .NET service tests
dotnet test services/identity/tests/Identity.Tests.csproj

# Build all .NET services
for svc in services/*/; do
  csproj=$(find "$svc" -maxdepth 1 -name "*.csproj" | head -1)
  [ -n "$csproj" ] && dotnet build "$csproj"
done
```

### Frontend Development

```bash
# Start the dev server with hot-reload
npx nx serve frontend

# Run frontend unit tests (Vitest)
npx nx test frontend

# Run frontend E2E tests (Playwright)
npx nx e2e frontend-e2e

# Production build
npx nx build frontend
```

### CDK Infrastructure

```bash
# Synthesize CloudFormation templates
cd infra && npx cdk synth

# Deploy to LocalStack
cdklocal deploy --all --context localstack=true --require-approval never

# Deploy to production AWS
cdk deploy --all

# Diff changes before deploying
cdklocal diff --all --context localstack=true
```

### LocalStack Management

```bash
# Start LocalStack
docker compose up -d

# Stop LocalStack
docker compose down

# Stop and remove all data
docker compose down -v

# View LocalStack logs
docker compose logs -f localstack

# Check LocalStack health
curl http://localhost:4566/_localstack/health
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AWS_ENDPOINT_URL` | `http://localhost:4566` | LocalStack endpoint (omit in production) |
| `AWS_REGION` | `us-east-1` | AWS region for all services |
| `AWS_ACCESS_KEY_ID` | `test` | LocalStack default credential |
| `AWS_SECRET_ACCESS_KEY` | `test` | LocalStack default credential |
| `COGNITO_USER_POOL_ID` | — | Cognito user pool ID (set after CDK deploy) |
| `API_GATEWAY_URL` | — | HTTP API Gateway base URL |
| `IS_LOCAL` | `true` | Flag indicating LocalStack environment |
| `VITE_API_URL` | `http://localhost:4566` | Frontend API base URL (Vite env prefix) |

> **Security Note:** Database connection strings (`DB_CONNECTION_STRING`) and Cognito client secrets (`COGNITO_CLIENT_SECRET`) are stored as SSM Parameter Store SecureString values — never as environment variables.

## API Versioning

All API endpoints are versioned using path-based prefixes:

```
https://<api-gateway-url>/v1/identity/users
https://<api-gateway-url>/v1/entities
https://<api-gateway-url>/v1/crm/contacts
```

## Event-Driven Communication

Services communicate asynchronously via SNS topics and SQS queues using the naming convention:

```
{domain}.{entity}.{action}
```

Examples: `crm.contact.created`, `invoicing.invoice.paid`, `workflow.task.completed`

All event schemas are defined in `libs/shared-schemas/src/events/` as JSON Schema documents.

## Testing

| Test Type | Tool | Command |
|-----------|------|---------|
| .NET Unit Tests | xUnit + Moq + FluentAssertions | `dotnet test services/<svc>/tests/*.Tests.csproj` |
| Frontend Unit Tests | Vitest + Testing Library | `npx nx test frontend` |
| Frontend E2E Tests | Playwright | `npx nx e2e frontend-e2e` |
| Integration Tests | xUnit against LocalStack | `npx nx run-many --target=test --all` |
| Contract Tests | JSON Schema validation | Per-service in `tests/` directories |

> **Important:** All integration and E2E tests run against LocalStack. No mocked AWS SDK calls are used in integration tests.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE.txt](LICENSE.txt) for the full license text.
