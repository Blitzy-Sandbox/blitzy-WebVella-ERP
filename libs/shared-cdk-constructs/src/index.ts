/**
 * @module @webvella-erp/shared-cdk-constructs
 * @description Barrel export (public API surface) for the shared CDK constructs library.
 *
 * Re-exports all L3 CDK constructs used by the 13 CDK stacks in infra/src/stacks/
 * for provisioning standardized AWS resources across all WebVella ERP bounded-context
 * microservices. Consumers import from `@webvella-erp/shared-cdk-constructs` via
 * the tsconfig.base.json path alias which resolves to this file.
 *
 * Exported constructs:
 *
 * - **LambdaServiceConstruct** — Standard Lambda + IAM role + CloudWatch log group
 *   for all 10 .NET 9 AOT services and the Node.js 22 authorizer. Replaces the
 *   monolith Startup.cs service wiring with per-service Lambda deployments.
 *
 * - **DynamoDbTableConstruct** — Single-table design DynamoDB tables replacing the
 *   monolith's single PostgreSQL database (DbContext.cs, DbRepository.cs). Used by
 *   8 services (all except Invoicing and Reporting which use RDS PostgreSQL).
 *
 * - **EventBusConstruct** — SNS/SQS event bus infrastructure replacing the monolith's
 *   HookManager reflection-based hook system and PostgreSQL LISTEN/NOTIFY pub/sub.
 *
 * @see AAP §0.4.1 — Target monorepo structure
 * @see AAP §0.5.1 — libs/shared-cdk-constructs/src/*.ts
 */

// Lambda service construct: LambdaServiceConstruct, LambdaServiceProps, LambdaServiceRuntime
export * from './lambda-service';

// DynamoDB table construct: DynamoDbTableConstruct, DynamoDbTableProps, GsiDefinition
export * from './dynamodb-table';

// SNS/SQS event bus construct: EventBusConstruct, EventBusProps, SqsSubscriptionDefinition
export * from './event-bus';
