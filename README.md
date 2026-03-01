[NEW PROJECT ALERT] Check out our new project for [Data collaboration - Tefter.bg](https://github.com/WebVella/WebVella.Tefter).

[NEW PROJECT ALERT] Check out our new project for [Document template generation](https://github.com/WebVella/WebVella.DocumentTemplates).

---

[![Project Homepage](https://img.shields.io/badge/Homepage-blue?style=for-the-badge)](https://webvella.com)
[![Dotnet](https://img.shields.io/badge/platform-.NET-blue?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![GitHub Repo stars](https://img.shields.io/github/stars/WebVella/WebVella-ERP?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/stargazers)
[![Nuget version](https://img.shields.io/nuget/v/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![Nuget download](https://img.shields.io/nuget/dt/WebVella.ERP?style=for-the-badge)](https://www.nuget.org/packages/WebVella.ERP)
[![WebVella Document Templates License](https://img.shields.io/badge/MIT-green?style=for-the-badge)](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt)

---

WebVella ERP 
======
**WebVella ERP** is a free and open-source web software, that targets extreme customization and plugability in service of any business data management needs. It is build upon our experience, best practices and the newest available technologies. The platform has been decomposed from a modular monolith into a suite of independently deployable, domain-aligned cloud-native microservices. It targets .NET 10 LTS with ASP.NET Core 10, using a database-per-service model backed by PostgreSQL 16+. Services communicate via REST/gRPC APIs and asynchronous event-driven messaging. Deployment is containerized with Docker and orchestratable via Kubernetes. Targets Linux or Windows as host OS.

If you want this project to continue or just like it, we will greatly appreciate your support of the project by: 
* giving it a "star" 
* contributing to the source
* Become a Sponsor: Click on the Sponsor button and Thank you in advance

Related repositories

[WebVella-ERP-StencilJs](https://github.com/WebVella/WebVella-ERP-StencilJs)

[WebVella-ERP-Seed](https://github.com/WebVella/WebVella-ERP-Seed)

[WebVella-TagHelpers](https://github.com/WebVella/TagHelpers)

## Architecture Overview

The WebVella ERP platform is decomposed into seven independently deployable microservices, a shared kernel library, and an API gateway:

| Service | Path | Responsibility |
|---------|------|----------------|
| **Core Platform Service** | `src/Services/WebVella.Erp.Service.Core/` | Entity management, record CRUD, security/identity, file storage, data sources, search |
| **CRM Service** | `src/Services/WebVella.Erp.Service.Crm/` | Accounts, contacts, cases, addresses, salutations, CRM search indexing |
| **Project/Task Service** | `src/Services/WebVella.Erp.Service.Project/` | Tasks, timelogs, comments, activity feed, project reporting |
| **Mail/Notification Service** | `src/Services/WebVella.Erp.Service.Mail/` | Email queue processing, SMTP engine, mail entity management |
| **Reporting Service** | `src/Services/WebVella.Erp.Service.Reporting/` | Report aggregation, cross-service data projections |
| **Admin/SDK Service** | `src/Services/WebVella.Erp.Service.Admin/` | Admin console, code generation, log management |
| **API Gateway** | `src/Gateway/WebVella.Erp.Gateway/` | Request routing, authentication middleware, Razor Pages BFF |

Each service owns its own PostgreSQL database schema and communicates with other services exclusively through REST/gRPC APIs and asynchronous domain events published via a message broker (RabbitMQ for local development, SNS+SQS for cloud/LocalStack validation).

## Shared Kernel

The **Shared Kernel** library (`src/SharedKernel/WebVella.Erp.SharedKernel/`) contains cross-cutting contracts and stateless utilities shared across all services:

- **Contracts/Events/** — Domain event base types and interfaces (converted from the monolith's hook system)
- **Models/** — Shared DTOs: `EntityRecord`, `EntityRecordList`, `BaseResponseModel`, `ErpUser`, `ErpRole`, `SystemIds`
- **Eql/** — The Entity Query Language (EQL) engine: grammar, AST, builder, and SQL generator
- **Database/** — Shared DDL/DML helpers (`DbRepository`), connection wrapper (`DbConnection`), type mapping (`DBTypeConverter`)
- **Security/** — JWT-based `SecurityContext` for cross-service identity propagation, `JwtTokenHandler`
- **Utilities/** — Crypto, datetime, JSON, and dynamic object helpers
- **Exceptions/** — `StorageException`, `ValidationException`, and other shared error types
- **Fts/** — Bulgarian full-text search rules and configuration

The Shared Kernel contains only pure contracts, interfaces, and stateless utilities — no service logic or database access.

## Repository Structure

```
webvella-erp-microservices/
├── src/
│   ├── SharedKernel/             — Shared contracts, EQL engine, utilities
│   │   └── WebVella.Erp.SharedKernel/
│   ├── Services/                 — 6 domain-aligned microservices + Admin
│   │   ├── WebVella.Erp.Service.Core/
│   │   ├── WebVella.Erp.Service.Crm/
│   │   ├── WebVella.Erp.Service.Project/
│   │   ├── WebVella.Erp.Service.Mail/
│   │   ├── WebVella.Erp.Service.Reporting/
│   │   └── WebVella.Erp.Service.Admin/
│   └── Gateway/                  — API Gateway / BFF
│       └── WebVella.Erp.Gateway/
├── proto/                        — gRPC service definitions (.proto files)
├── tests/                        — Test projects per service + integration tests
│   ├── WebVella.Erp.Tests.Core/
│   ├── WebVella.Erp.Tests.Crm/
│   ├── WebVella.Erp.Tests.Project/
│   ├── WebVella.Erp.Tests.Mail/
│   ├── WebVella.Erp.Tests.Reporting/
│   ├── WebVella.Erp.Tests.Admin/
│   ├── WebVella.Erp.Tests.Gateway/
│   ├── WebVella.Erp.Tests.SharedKernel/
│   └── WebVella.Erp.Tests.Integration/
├── infrastructure/               — Docker, Kubernetes, LocalStack configs
│   ├── localstack/
│   ├── kubernetes/
│   └── scripts/
├── docker-compose.yml            — Full orchestration: all services + infra
├── docker-compose.localstack.yml — LocalStack overrides for cloud validation
├── docker-compose.override.yml   — Development overrides
├── Directory.Build.props         — Centralized build properties
├── Directory.Packages.props      — Central Package Management
└── .github/workflows/            — CI/CD pipeline definitions
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (LTS)
- [Docker](https://docs.docker.com/get-docker/) and [Docker Compose](https://docs.docker.com/compose/install/)
- PostgreSQL 16+ (provided via Docker Compose for local development)

### Quick Start — Run All Services

```bash
# Clone the repository
git clone https://github.com/WebVella/WebVella-ERP.git
cd WebVella-ERP

# Start all services with Docker Compose (includes PostgreSQL, Redis, RabbitMQ)
docker-compose up
```

The API Gateway will be available at `http://localhost:5000`. All existing REST API v3 endpoints (`/api/v3/{locale}/...`) are routed through the gateway to the appropriate backend microservice.

### Individual Service Development

```bash
# Build the entire solution
dotnet restore WebVella.ERP3.sln
dotnet build WebVella.ERP3.sln

# Run a specific service locally (example: Core service)
cd src/Services/WebVella.Erp.Service.Core
dotnet run
```

### LocalStack Validation

To validate cloud-native features (SNS, SQS, S3) against LocalStack:

```bash
docker-compose -f docker-compose.yml -f docker-compose.localstack.yml up
```

This brings up a fully functional stack with all services, infrastructure, and LocalStack emulating AWS services on port 4566.

## Technology Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 10 LTS | Runtime and SDK |
| ASP.NET Core | 10 | Web framework for REST APIs and Razor Pages |
| PostgreSQL | 16+ | Database-per-service (one instance per microservice) |
| Redis | 7 | Distributed caching for entity metadata and sessions |
| RabbitMQ | 3 | Message broker for local/Docker inter-service events |
| AWS SNS + SQS | via LocalStack | Cloud event topics and queues (validated with LocalStack) |
| AWS S3 | via LocalStack | Cloud file storage (validated with LocalStack) |
| gRPC | — | Inter-service synchronous communication |
| MassTransit | 8.4 | Message bus abstraction (RabbitMQ + SNS/SQS transports) |
| Docker | — | Service containerization |
| Kubernetes | — | Container orchestration for production deployment |
| xUnit | 2.9 | Unit and integration test framework |
| Testcontainers | 4.10 | Docker container management for integration tests |

## Testing

The project includes a comprehensive test suite organized by service:

```bash
# Run all tests
dotnet test WebVella.ERP3.sln

# Run tests for a specific service
dotnet test tests/WebVella.Erp.Tests.Core/
dotnet test tests/WebVella.Erp.Tests.Crm/
dotnet test tests/WebVella.Erp.Tests.Project/
dotnet test tests/WebVella.Erp.Tests.Mail/

# Run cross-service integration tests (requires Docker)
dotnet test tests/WebVella.Erp.Tests.Integration/
```

- **Unit tests** — Business logic validation per service using xUnit, Moq, and FluentAssertions
- **Integration tests** — API endpoint testing using `WebApplicationFactory` and `Testcontainers.PostgreSql`
- **Cross-service tests** — End-to-end business rule validation using `Testcontainers.LocalStack` for message broker and storage emulation
- **Schema migration tests** — Verify zero data loss during database-per-service migration

## CI/CD

Automated pipelines are defined in `.github/workflows/`:

| Workflow | File | Purpose |
|----------|------|---------|
| Continuous Integration | `.github/workflows/ci.yml` | Build, lint, and test all services on every push/PR |
| Continuous Deployment | `.github/workflows/cd.yml` | Build Docker images, push to registry, deploy to Kubernetes |
| LocalStack Validation | `.github/workflows/localstack-validation.yml` | End-to-end tests against the full LocalStack-backed Docker Compose stack |

## Legacy Monolith (Reference)

The original monolith source folders remain in the repository as a reference during the migration process. These folders contain the source implementations from which the microservices were extracted:

| Legacy Folder | Description |
|---------------|-------------|
| `WebVella.Erp/` | Core engine — entity management, EQL, hooks, jobs, database access |
| `WebVella.Erp.Web/` | Web layer — REST API v3 surface, Razor Pages, middleware, services |
| `WebVella.Erp.Plugins.SDK/` | SDK plugin — admin console, code generation |
| `WebVella.Erp.Plugins.Next/` | Next plugin — CRM entity provisioning, search indexing |
| `WebVella.Erp.Plugins.Project/` | Project plugin — task management, timelogs, reporting |
| `WebVella.Erp.Plugins.Mail/` | Mail plugin — SMTP engine, email queue |
| `WebVella.Erp.Plugins.Crm/` | CRM plugin — CRM skeleton with patch framework |
| `WebVella.Erp.Plugins.MicrosoftCDM/` | CDM plugin — Microsoft Common Data Model integration |
| `WebVella.Erp.Site*/` | Site host variants — composition root references |
| `WebVella.Erp.ConsoleApp/` | Console harness — service bootstrap reference |

These legacy folders are retained for traceability and to support incremental migration validation. New development should target the microservice projects under `src/`.

### Third party libraries
* see [LIBRARIES](https://github.com/WebVella/WebVella-ERP/blob/master/LIBRARIES.md) files

## License 
* see [LICENSE](https://github.com/WebVella/WebVella-ERP/blob/master/LICENSE.txt) file

## Contact
#### Developer/Company
* Homepage: [webvella.com](http://webvella.com)
* Twitter: [@webvella](https://twitter.com/webvella "webvella on twitter")



