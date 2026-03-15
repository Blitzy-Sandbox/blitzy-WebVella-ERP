#!/usr/bin/env node

/**
 * WebVella ERP — CDK Application Entry Point
 *
 * This file is the AWS CDK application root, replacing the monolith's
 * `Program.cs` (WebHost bootstrapper) and `Startup.cs` (DI composition
 * + HTTP pipeline configuration) with infrastructure-as-code stack
 * instantiation for the entire serverless microservices platform.
 *
 * ## Transformation Mapping
 *
 * **From `WebVella.Erp.Site/Program.cs`:**
 * The monolith used `WebHost.CreateDefaultBuilder(args).UseStartup<Startup>().Build().Run()`
 * as its single entry point. This CDK app replaces it by synthesizing CloudFormation
 * templates for 13 stacks that collectively define all AWS resources.
 *
 * **From `WebVella.Erp.Site/Startup.cs`:**
 * - `ConfigureServices()` (lines 37-129) registered all monolith services into a
 *   single DI container → replaced by 10 bounded-context CDK stacks, each owning
 *   its own Lambda functions, datastores, and IAM policies.
 * - `services.AddAuthentication(...)` (lines 88-125) configured Cookie+JWT dual-auth
 *   with policy scheme `JWT_OR_COOKIE` → replaced by SharedStack (Cognito user pool)
 *   + IdentityStack (auth Lambda) + ApiGatewayStack (JWT authorizer).
 * - `services.AddErp()` (line 128) initialized all ERP subsystems → replaced by
 *   instantiating EntityManagement, CRM, Inventory, Invoicing, Reporting,
 *   Notifications, FileManagement, Workflow, and PluginSystem stacks.
 * - `Configure()` (lines 132-194) composed the HTTP pipeline (routing, auth, CORS,
 *   middleware) → replaced by ApiGatewayStack (HTTP API v2 with path-based routing).
 * - `app.UseErpPlugin<SdkPlugin>()` (line 183) → replaced by PluginSystemStack.
 * - `app.UseJwtMiddleware()` (line 186) → replaced by API Gateway JWT authorizer
 *   with custom Lambda authorizer fallback for LocalStack.
 *
 * **From `WebVella.Erp.Site/Config.json`:**
 * - `ConnectionString` → per-service DynamoDB/RDS resources in each stack.
 * - `EncryptionKey` → SSM SecureString parameter in SharedStack.
 * - `Jwt` section → Cognito user pool JWKS-based token validation in SharedStack.
 * - `EmailSMTPServerName`, etc. → NotificationsStack configuration.
 * - `EnableFileSystemStorage` → FileManagementStack S3 bucket.
 * - Feature flags (`DevelopmentMode`, `EnableBackgroundJobs`) → SSM parameters.
 *
 * ## Dual-Target Deployment (AAP §0.7.6)
 *
 * This file supports deployment to both LocalStack and production AWS using
 * a single codebase. The `localstack` CDK context flag controls resource
 * configuration:
 * - `cdklocal deploy --context localstack=true` → LocalStack (account 000000000000)
 * - `cdk deploy` → Production AWS (CDK_DEFAULT_ACCOUNT)
 *
 * ## Stack Dependency Chain
 *
 * ```
 * SharedStack (root — Cognito, SNS event bus, SSM params, VPC)
 *   ├── IdentityStack (user/role management, Cognito integration)
 *   ├── EntityManagementStack (entity/field/relation/record CRUD)
 *   ├── CrmStack (accounts, contacts, addresses)
 *   ├── InventoryStack (tasks, timelogs, products, stock)
 *   ├── InvoicingStack (invoices, payments — RDS PostgreSQL)
 *   ├── ReportingStack (analytics, read models — RDS PostgreSQL)
 *   ├── NotificationsStack (email, webhooks, push)
 *   ├── FileManagementStack (S3 file storage)
 *   ├── WorkflowStack (Step Functions orchestration)
 *   └── PluginSystemStack (plugin registry, extensions)
 *         └── ApiGatewayStack (HTTP API v2, route-to-Lambda mapping)
 *               └── FrontendStack (S3 static SPA hosting)
 * ```
 *
 * @module infra/src/app
 */

import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';

// ---------------------------------------------------------------------------
// Stack Imports — one per bounded context + shared infrastructure
// ---------------------------------------------------------------------------

import { SharedStack } from './stacks/shared-stack';
import { IdentityStack } from './stacks/identity-stack';
import { EntityManagementStack } from './stacks/entity-management-stack';
import { CrmStack } from './stacks/crm-stack';
import { InventoryStack } from './stacks/inventory-stack';
import { InvoicingStack } from './stacks/invoicing-stack';
import { ReportingStack } from './stacks/reporting-stack';
import { NotificationsStack } from './stacks/notifications-stack';
import { FileManagementStack } from './stacks/file-management-stack';
import { WorkflowStack } from './stacks/workflow-stack';
import { PluginSystemStack } from './stacks/plugin-system-stack';
import { ApiGatewayStack } from './stacks/api-gateway-stack';
import { FrontendStack } from './stacks/frontend-stack';

// ---------------------------------------------------------------------------
// CDK Application Bootstrap
// ---------------------------------------------------------------------------

/**
 * Create the CDK application instance.
 * This replaces `WebHost.CreateDefaultBuilder(args)` from Program.cs.
 */
const app = new cdk.App();

// ---------------------------------------------------------------------------
// Dual-Target Configuration (AAP §0.7.6)
// ---------------------------------------------------------------------------

/**
 * Determine deployment target from CDK context.
 *
 * Usage:
 * - LocalStack:  `cdklocal deploy --context localstack=true`
 * - Production:  `cdk deploy` (defaults to false via cdk.json)
 *
 * The cdk.json file sets `"localstack": "false"` as the default context value.
 * Override with `--context localstack=true` for LocalStack deployments.
 *
 * This flag is passed to every stack to control conditional resource creation:
 * - Removal policies (DESTROY vs RETAIN)
 * - Password policies (relaxed vs strict)
 * - VPC configuration (default vs dedicated)
 * - Lambda tracing (structured logging vs X-Ray)
 * - Encryption settings (default vs customer-managed KMS)
 */
const isLocalStack: boolean = app.node.tryGetContext('localstack') === 'true';

/**
 * AWS environment configuration.
 *
 * LocalStack:
 * - Account: '000000000000' (LocalStack's fixed account ID)
 * - Region: 'us-east-1' (LocalStack default region per AAP §0.8.6)
 *
 * Production:
 * - Account: CDK_DEFAULT_ACCOUNT environment variable (set by `cdk deploy`)
 * - Region: CDK_DEFAULT_REGION or 'us-east-1' fallback
 */
const env: cdk.Environment = {
  account: isLocalStack ? '000000000000' : process.env.CDK_DEFAULT_ACCOUNT,
  region: isLocalStack ? 'us-east-1' : (process.env.CDK_DEFAULT_REGION || 'us-east-1'),
};

// ---------------------------------------------------------------------------
// Stack Naming Convention
// ---------------------------------------------------------------------------

/**
 * All stacks use the `WebVellaErp` prefix for consistent CloudFormation
 * stack identification. This mirrors the monolith's `WebVella.Erp.*`
 * namespace convention adapted for CDK stack IDs.
 */
const STACK_PREFIX = 'WebVellaErp';

// ---------------------------------------------------------------------------
// 1. SHARED STACK — Foundation Resources (no dependencies)
// ---------------------------------------------------------------------------

/**
 * SharedStack provides the foundation resources consumed by ALL other stacks:
 * - Cognito User Pool (replaces Startup.cs Cookie+JWT dual-scheme auth)
 * - SNS Domain Event Bus (replaces HookManager post-hooks + LISTEN/NOTIFY)
 * - SSM Parameter Store entries (replaces Config.json settings)
 * - VPC for RDS-backed services (Invoicing, Reporting)
 *
 * Maps to: Config.json settings migration + Startup.cs service registration
 */
const sharedStack = new SharedStack(app, `${STACK_PREFIX}Shared`, {
  env,
  isLocalStack,
  description: 'WebVella ERP — Shared resources: Cognito, SNS event bus, SSM parameters, VPC',
});

// ---------------------------------------------------------------------------
// 2. IDENTITY STACK — Authentication & User Management
// ---------------------------------------------------------------------------

/**
 * IdentityStack handles authentication and user/role management:
 * - Lambda handlers for login/logout/token-refresh via Cognito
 * - User CRUD backed by Cognito + DynamoDB
 * - Role management via Cognito groups + DynamoDB
 *
 * Maps to: Startup.cs auth config (lines 88-125), AuthService.cs,
 * SecurityManager.cs, SecurityContext.cs, JwtMiddleware.cs
 *
 * Replaces the monolith's Cookie+JWT dual-scheme auth with Cognito
 * user pool authentication. MD5-hashed passwords (SecurityManager.cs)
 * are migrated via a user migration Lambda trigger on the Cognito pool.
 */
const identityStack = new IdentityStack(app, `${STACK_PREFIX}Identity`, {
  env,
  isLocalStack,
  userPool: sharedStack.userPool,
  userPoolClientId: sharedStack.userPoolClient.userPoolClientId,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Identity & Access Management: auth, users, roles',
});
identityStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 3. ENTITY MANAGEMENT STACK — Core Entity Engine
// ---------------------------------------------------------------------------

/**
 * EntityManagementStack is the LARGEST bounded context, replacing the monolith's
 * core entity/field/relation/record management subsystem:
 * - 7 Lambda handlers for entity, field, relation, record, datasource, search,
 *   and import/export operations
 * - DynamoDB tables for metadata and record storage (single-table design)
 * - DynamoDB Streams for change data capture events
 *
 * Maps to: EntityManager.cs, RecordManager.cs, EntityRelationManager.cs,
 * DataSourceManager.cs, SearchManager.cs, ImportExportManager.cs,
 * EqlBuilder.cs (EQL → DynamoDB query adapter per AAP §0.7.1),
 * Cache.cs, DbEntityRepository.cs, DbRecordRepository.cs
 *
 * The EQL engine (13 source files) is decomposed per AAP §0.7.1:
 * EQL-like query syntax translates to DynamoDB Query/Scan operations.
 */
const entityManagementStack = new EntityManagementStack(app, `${STACK_PREFIX}EntityManagement`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Entity Management: entity/field/relation/record CRUD',
});
entityManagementStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 4. CRM STACK — Contacts & Account Management
// ---------------------------------------------------------------------------

/**
 * CrmStack handles CRM domain operations:
 * - Account CRUD (company/person types)
 * - Contact CRUD with salutation support
 * - Address association and search indexing
 *
 * Maps to: CrmPlugin.cs, NextPlugin.20190204.cs (account/contact/address
 * entity creation), NextPlugin.20190206.cs (salutation entity),
 * SearchService.cs (x_search field indexing), Configuration.cs
 * (AccountSearchIndexFields, ContactSearchIndexFields)
 *
 * Post-create/update hooks from Hooks/Api/ are migrated to SNS domain
 * events: crm.account.{created|updated|deleted}, crm.contact.{created|updated|deleted}
 */
const crmStack = new CrmStack(app, `${STACK_PREFIX}Crm`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — CRM: accounts, contacts, addresses',
});
crmStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 5. INVENTORY STACK — Project Management & Product Tracking
// ---------------------------------------------------------------------------

/**
 * InventoryStack handles project management and inventory operations:
 * - Task CRUD with status management and key generation
 * - Timelog operations with billable hour tracking
 * - Product catalog and stock level management
 *
 * Maps to: ProjectPlugin.cs + 9 patch files, TaskService.cs,
 * TimelogService.cs, CommentService.cs, FeedService.cs,
 * ReportingService.cs, ProjectController.cs (api/v3.0/p/project/*),
 * StartTasksOnStartDate scheduled job (→ Step Functions via Workflow service)
 */
const inventoryStack = new InventoryStack(app, `${STACK_PREFIX}Inventory`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Inventory: tasks, timelogs, products, stock',
});
inventoryStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 6. INVOICING STACK — Billing & Payments (RDS PostgreSQL — ACID-critical)
// ---------------------------------------------------------------------------

/**
 * InvoicingStack handles ACID-critical billing operations:
 * - Invoice CRUD with transactional guarantees
 * - Payment processing with double-entry bookkeeping
 * - RDS PostgreSQL (not DynamoDB) for ACID compliance per AAP §0.4.2
 *
 * Maps to: RecordManager.cs invoice/payment workflows requiring
 * multi-statement transactions, which DynamoDB cannot provide.
 * Uses FluentMigrator for schema-isolated RDS PostgreSQL tables.
 *
 * This is one of two services using RDS PostgreSQL (the other is Reporting).
 * Database-per-service isolation via separate RDS instances per AAP §0.4.2.
 */
const invoicingStack = new InvoicingStack(app, `${STACK_PREFIX}Invoicing`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Invoicing: invoices, payments (RDS PostgreSQL)',
});
invoicingStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 7. REPORTING STACK — Analytics & Read Models (RDS PostgreSQL)
// ---------------------------------------------------------------------------

/**
 * ReportingStack handles analytics and event-sourced read model projections:
 * - Report generation from read-optimized RDS PostgreSQL projections
 * - SQS consumer for domain events from all services (CQRS read side)
 * - Dashboard data aggregation
 *
 * Maps to: DataSourceManager.cs (datasource execution engine),
 * RecordHookManager.cs (hook-based event consumption for read model updates),
 * ReportingService.cs from Project plugin
 *
 * CQRS pattern per AAP §0.4.2: events from all domains are consumed via
 * SQS and projected into read-optimized RDS PostgreSQL tables. This is
 * the second service using RDS PostgreSQL (the other is Invoicing).
 */
const reportingStack = new ReportingStack(app, `${STACK_PREFIX}Reporting`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Reporting: analytics, dashboards, read models (RDS PostgreSQL)',
});
reportingStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 8. NOTIFICATIONS STACK — Email, Webhooks & Push
// ---------------------------------------------------------------------------

/**
 * NotificationsStack handles all notification channels:
 * - Email send/queue operations (replacing MailPlugin SMTP engine)
 * - Webhook dispatch for external integrations
 * - SQS-triggered queue processor for async email delivery
 * - DLQ for failed notification deliveries per AAP §0.8.5
 *
 * Maps to: MailPlugin.cs + 7 patches (email/smtp_service entities),
 * SmtpService.cs (SMTP engine: validation, send, queue),
 * MailPlugin Jobs/ (scheduled SMTP queue processor),
 * Notifications/ (PostgreSQL LISTEN/NOTIFY → SNS/SQS replacement)
 *
 * The monolith's PostgreSQL LISTEN/NOTIFY pub/sub is replaced by
 * SNS topics for domain events and SQS queues for consumer decoupling.
 */
const notificationsStack = new NotificationsStack(app, `${STACK_PREFIX}Notifications`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Notifications: email, webhooks, push notifications',
});
notificationsStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 9. FILE MANAGEMENT STACK — S3-Based Document Storage
// ---------------------------------------------------------------------------

/**
 * FileManagementStack handles file upload, download, and metadata:
 * - S3 bucket for file content storage (replaces LO/filesystem/blob backends)
 * - DynamoDB table for file metadata
 * - Presigned URL generation for secure direct uploads/downloads
 *
 * Maps to: DbFileRepository.cs (file lifecycle: LO, filesystem, blob via
 * Storage.Net), DbFile.cs (file metadata model), UserFileService.cs
 * (user file upload/finalization), FileService.cs (embedded resources)
 *
 * The monolith supported three storage backends: PostgreSQL Large Objects,
 * filesystem, and Storage.Net blob providers. All are unified into S3.
 */
const fileManagementStack = new FileManagementStack(app, `${STACK_PREFIX}FileManagement`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — File Management: S3 storage, metadata, presigned URLs',
});
fileManagementStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 10. WORKFLOW STACK — Step Functions Orchestration
// ---------------------------------------------------------------------------

/**
 * WorkflowStack handles background job orchestration:
 * - Step Functions state machines for cross-domain workflows
 * - Lambda handlers for individual workflow steps
 * - Saga pattern for multi-service operations (e.g., invoice → inventory → notify)
 *
 * Maps to: JobManager.cs (singleton coordinator, job type registry),
 * JobPool.cs (bounded 20-thread executor), SheduleManager.cs (schedule
 * plan CRUD + trigger), JobDataService.cs (PostgreSQL job/schedule persistence),
 * ErpBackgroundServices.cs (Generic Host BackgroundService)
 *
 * The monolith's in-process JobManager/JobPool with 20-thread bounded
 * executor is replaced by AWS Step Functions for durable, visible,
 * and retry-capable workflow orchestration per AAP §0.4.2 Saga Pattern.
 */
const workflowStack = new WorkflowStack(app, `${STACK_PREFIX}Workflow`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Workflow: Step Functions orchestration, scheduled jobs',
});
workflowStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 11. PLUGIN SYSTEM STACK — Extension Registry
// ---------------------------------------------------------------------------

/**
 * PluginSystemStack handles plugin registration and management:
 * - Plugin registry with metadata persistence in DynamoDB
 * - Plugin listing and configuration management
 * - Extension point definition and discovery
 *
 * Maps to: ErpPlugin.cs (abstract plugin base with JSON metadata persistence),
 * IErpService.cs (plugin initialization contract), SdkPlugin.cs + patches
 * (SDK admin console), plugin_data table (DynamoDB), app/app_page/
 * app_sitemap_* tables (DynamoDB)
 *
 * The monolith's reflection-based `ErpPlugin` discovery and `IErpService`
 * initialization contracts are replaced by a REST API for plugin CRUD,
 * backed by DynamoDB for metadata persistence.
 */
const pluginSystemStack = new PluginSystemStack(app, `${STACK_PREFIX}PluginSystem`, {
  env,
  isLocalStack,
  eventBus: sharedStack.eventBus,
  description: 'WebVella ERP — Plugin System: extension registry, configuration',
});
pluginSystemStack.addDependency(sharedStack);

// ---------------------------------------------------------------------------
// 12. API GATEWAY STACK — HTTP API v2 Entry Point
// ---------------------------------------------------------------------------

/**
 * ApiGatewayStack defines the single HTTP API v2 entry point for all services:
 * - Path-based routing to per-domain Lambda functions (AAP §0.4.2)
 * - JWT authorization via Cognito (with custom Lambda authorizer fallback
 *   for LocalStack per AAP §0.7.6)
 * - CORS configuration (replaces Startup.cs AddCors policy)
 * - API versioning via /v1/ path prefix (AAP §0.8.6)
 *
 * Maps to: WebApiController.cs (100+ endpoints in a single controller,
 * remapped to per-domain Lambda handlers), Startup.cs routing/auth/CORS
 * pipeline (lines 132-194), ErpMiddleware.cs (per-request context →
 * per-invocation Lambda event context), JwtMiddleware.cs (Bearer token
 * validation → API Gateway JWT authorizer)
 *
 * All 10 bounded-context stacks export their `functions` arrays, which
 * this stack uses to create Lambda integrations with the HTTP API.
 */
const apiGatewayStack = new ApiGatewayStack(app, `${STACK_PREFIX}ApiGateway`, {
  env,
  isLocalStack,
  userPool: sharedStack.userPool,
  identityFunctions: identityStack.functions,
  entityManagementFunctions: entityManagementStack.functions,
  crmFunctions: crmStack.functions,
  inventoryFunctions: inventoryStack.functions,
  invoicingFunctions: invoicingStack.functions,
  reportingFunctions: reportingStack.functions,
  notificationsFunctions: notificationsStack.functions,
  fileManagementFunctions: fileManagementStack.functions,
  workflowFunctions: workflowStack.functions,
  pluginSystemFunctions: pluginSystemStack.functions,
  description: 'WebVella ERP — API Gateway: HTTP API v2, routing, JWT auth, CORS',
});

// ApiGatewayStack depends on all service stacks that provide Lambda functions
apiGatewayStack.addDependency(identityStack);
apiGatewayStack.addDependency(entityManagementStack);
apiGatewayStack.addDependency(crmStack);
apiGatewayStack.addDependency(inventoryStack);
apiGatewayStack.addDependency(invoicingStack);
apiGatewayStack.addDependency(reportingStack);
apiGatewayStack.addDependency(notificationsStack);
apiGatewayStack.addDependency(fileManagementStack);
apiGatewayStack.addDependency(workflowStack);
apiGatewayStack.addDependency(pluginSystemStack);

// ---------------------------------------------------------------------------
// 13. FRONTEND STACK — S3 Static SPA Hosting
// ---------------------------------------------------------------------------

/**
 * FrontendStack hosts the React 19 SPA as static assets on S3:
 * - S3 bucket configured for static website hosting
 * - Conditional CloudFront distribution for production (skipped on LocalStack)
 * - API Gateway URL injected for frontend configuration
 *
 * Maps to: The monolith's Razor Pages + ViewComponents + jQuery + StencilJS
 * frontend, which is entirely replaced by a React 19 SPA built with Vite 6
 * and deployed as static assets to S3. The _AppMaster.cshtml layout chrome,
 * 50+ Pc* ViewComponents, and 25+ PcField* components are reimplemented
 * as React components in the apps/frontend/ project.
 *
 * This is a pure static SPA per AAP §0.8.1: zero server-side rendering,
 * zero Lambda@Edge, zero API routes in the frontend application.
 */
const frontendStack = new FrontendStack(app, `${STACK_PREFIX}Frontend`, {
  env,
  isLocalStack,
  apiGatewayUrl: apiGatewayStack.apiUrl,
  description: 'WebVella ERP — Frontend: React 19 SPA on S3',
});
frontendStack.addDependency(apiGatewayStack);

// ---------------------------------------------------------------------------
// CDK Synthesis
// ---------------------------------------------------------------------------

/**
 * Synthesize all stacks into CloudFormation templates.
 *
 * Output directory: cdk.out/ (configured in cdk.json)
 *
 * Deployment commands:
 * - LocalStack: `cdklocal deploy --all --context localstack=true`
 * - Production: `cdk deploy --all`
 * - Single stack: `cdklocal deploy WebVellaErpShared --context localstack=true`
 */
app.synth();
