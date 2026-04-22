/**
 * @file infra/src/constructs/index.ts
 * @description Barrel export for all reusable CDK L3 constructs in the WebVella ERP
 * serverless platform infrastructure layer.
 *
 * This barrel enables CDK stacks in `infra/src/stacks/` to import all construct
 * classes, props interfaces, and shared types from a single path:
 *
 * ```typescript
 * import {
 *   WebVellaLambdaService, LambdaRuntime,
 *   WebVellaDynamoDBTable, GsiDefinition,
 *   WebVellaEventBus, QueueSubscription,
 *   WebVellaApiIntegration, RouteDefinition,
 * } from '../constructs';
 * ```
 *
 * Replaces the monolith's Startup.cs DI composition (WebVella.Erp.Site/Startup.cs
 * lines 37-129) where all services were wired in a single ConfigureServices method.
 * In the CDK architecture, stacks compose AWS resources using these reusable constructs
 * instead of DI container registrations.
 *
 * Exported constructs:
 * - WebVellaLambdaService  — Standard Lambda function (.NET 9 AOT / Node.js 22)
 * - WebVellaDynamoDBTable  — Standard DynamoDB single-table design
 * - WebVellaEventBus       — Standard SNS/SQS event bus with DLQ
 * - WebVellaApiIntegration — Standard API Gateway v2 route integrations
 *
 * Design decisions:
 * - Explicit named exports (no wildcard `export *`) for optimal tree-shaking,
 *   IDE autocomplete, and compile-time safety
 * - TypeScript strict mode compatible
 * - No implementation code — pure re-exports only
 *
 * @module infra/src/constructs
 */

// ---------------------------------------------------------------------------
// Lambda Function Construct
// ---------------------------------------------------------------------------

export {
  WebVellaLambdaService,
  WebVellaLambdaServiceProps,
  LambdaRuntime,
} from './lambda-service';

// ---------------------------------------------------------------------------
// DynamoDB Table Construct
// ---------------------------------------------------------------------------

export {
  WebVellaDynamoDBTable,
  WebVellaDynamoDBTableProps,
  GsiDefinition,
} from './dynamodb-table';

// ---------------------------------------------------------------------------
// SNS/SQS Event Bus Construct
// ---------------------------------------------------------------------------

export {
  WebVellaEventBus,
  WebVellaEventBusProps,
  QueueSubscription,
} from './event-bus';

// ---------------------------------------------------------------------------
// API Gateway Integration Construct
// ---------------------------------------------------------------------------

export {
  WebVellaApiIntegration,
  WebVellaApiIntegrationProps,
  RouteDefinition,
} from './api-integration';
