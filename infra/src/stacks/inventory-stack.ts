/**
 * InventoryStack — Inventory / Project Management Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the Inventory / Project
 * Management bounded context, replacing the monolith's Project Management
 * plugin (`WebVella.Erp.Plugins.Project`) with a DynamoDB-backed serverless
 * service handling tasks, timelogs, products, and stock management.
 *
 * **Source systems replaced:**
 * - `ProjectPlugin.cs` + 9 patch files — Full project/task schema seeding with
 *   entity definitions for tasks, timelogs, comments, feeds, milestones, sprints.
 * - `TaskService.cs` — Task business logic (CRUD, calculation fields, status
 *   management, key generation from project abbreviation + task number).
 * - `TimelogService.cs` — Timelog CRUD operations and billable hour tracking.
 * - `ProjectController.cs` — `api/v3.0/p/project/*` HTTP endpoints for task
 *   CRUD, timelog operations, comments, and feed management.
 * - `CommentService.cs`, `FeedService.cs`, `ReportingService.cs` — Ancillary
 *   project management services embedded in the monolith's plugin system.
 * - Task/timelog lifecycle hooks (post-create, post-update) from
 *   `Hooks/Api/` — Migrated to SNS domain events.
 * - `StartTasksOnStartDate` scheduled job from `JobManager` — Migrated to
 *   Step Functions via the Workflow service.
 *
 * **Target architecture:**
 * - DynamoDB table for all inventory/project data (single-table design)
 * - 2 Lambda functions for domain-specific CRUD operations
 * - SNS domain events for cross-service communication
 * - SSM Parameter Store for resource discovery
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`erp-inventory-main`) — Single-table design storing all
 *    inventory and project management entities. Partition key patterns:
 *    - `TASK#{taskId}` — Task records (subject, number, key, status, project)
 *    - `TIMELOG#{timelogId}` — Timelog entries (hours, billable, user, task)
 *    - `PRODUCT#{productId}` — Product catalog entries
 *    - `STOCK#{stockId}` — Stock/inventory level records
 *    Sort key patterns: `META` for main records, time-based sort keys for
 *    timelogs and audit history.
 *    GSI1: `GSI1PK`/`GSI1SK` — Project-based task lookups (e.g., all tasks
 *    for a project), user-based timelog queries (e.g., all timelogs by user).
 *    GSI2: `GSI2PK`/`GSI2SK` — Status-based queries (e.g., all open tasks),
 *    date-range queries (e.g., timelogs within a date range).
 *
 * 2. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    - `webvella-erp-inventory-task` (512 MB, 30s) — Task CRUD, comment
 *      operations, and feed management. Replaces `TaskService.cs` business
 *      logic and `ProjectController.cs` task endpoints.
 *    - `webvella-erp-inventory-timelog` (512 MB, 30s) — Timelog CRUD
 *      operations. Replaces `TimelogService.cs` business logic and
 *      `ProjectController.cs` timelog endpoints.
 *
 * 3. **SSM Parameter** (`/webvella-erp/inventory/table-name`) — Stores the
 *    DynamoDB table name for cross-service discovery per AAP §0.8.6.
 *
 * Domain events published to the shared SNS event bus:
 * - `inventory.task.created` — New task created
 * - `inventory.task.updated` — Task updated (status change, field edit)
 * - `inventory.task.deleted` — Task deleted
 * - `inventory.timelog.created` — New timelog entry recorded
 * - `inventory.timelog.updated` — Timelog entry modified
 *
 * Source files referenced:
 * - WebVella.Erp.Plugins.Project/ProjectPlugin.cs — Plugin entry + schema seeding
 * - WebVella.Erp.Plugins.Project/Services/TaskService.cs — Task business logic
 * - WebVella.Erp.Plugins.Project/Services/TimelogService.cs — Timelog operations
 * - WebVella.Erp.Plugins.Project/Controllers/ProjectController.cs — HTTP endpoints
 *
 * @module infra/src/stacks/inventory-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';

import {
  WebVellaLambdaService,
  LambdaRuntime,
  WebVellaDynamoDBTable,
  GsiDefinition,
} from '../constructs';

// ---------------------------------------------------------------------------
// Interface: InventoryStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the InventoryStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface InventoryStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - Removal policies: DESTROY (LocalStack) vs RETAIN (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The TaskHandler and TimelogHandler Lambda
   * functions publish domain events to this topic using the naming
   * convention from AAP §0.8.5:
   * - `inventory.task.created`
   * - `inventory.task.updated`
   * - `inventory.task.deleted`
   * - `inventory.timelog.created`
   * - `inventory.timelog.updated`
   *
   * Replaces the monolith's synchronous post-CRUD hooks from
   * `WebVella.Erp.Plugins.Project/Hooks/Api/` with asynchronous SNS
   * event publishing per AAP §0.7.2 hook-to-event migration strategy.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: InventoryStack
// ---------------------------------------------------------------------------

/**
 * InventoryStack — CDK stack for the Inventory / Project Management
 * bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * table, Lambda functions, IAM policies, and SSM parameters. No other
 * service may directly access the inventory service's datastore.
 *
 * The stack exposes two public properties consumed by the ApiGatewayStack
 * for route-to-Lambda integration mapping:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 */
export class InventoryStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the TaskHandler and TimelogHandler functions that handle all
   * inventory service HTTP endpoints. Consumed by ApiGatewayStack for
   * path-based routing under `/v1/inventory/*`.
   *
   * Index 0: TaskHandler — task CRUD, comments, feed management
   * Index 1: TimelogHandler — timelog CRUD operations
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the inventory service datastore.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/inventory/table-name` for cross-service discovery.
   */
  public readonly tableName: string;

  constructor(scope: Construct, id: string, props: InventoryStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for inventory / project mgmt
    // -----------------------------------------------------------------------
    // Replaces PostgreSQL `rec_task`, `rec_timelog`, `rec_comment`,
    // `rec_project`, and related dynamic entity tables from the monolith's
    // Project Management plugin.
    //
    // Access patterns:
    //   PK=TASK#{taskId},       SK=META               → Task record
    //   PK=TASK#{taskId},       SK=COMMENT#{commentId} → Task comment
    //   PK=TASK#{taskId},       SK=FEED#{timestamp}    → Task feed entry
    //   PK=TIMELOG#{timelogId}, SK=META               → Timelog record
    //   PK=PRODUCT#{productId}, SK=META               → Product catalog entry
    //   PK=STOCK#{stockId},     SK=META               → Stock level record
    //
    // GSI1 — Project-based task lookups and user-based timelog queries:
    //   GSI1PK=PROJECT#{projectId}, GSI1SK=TASK#{taskId}     → Tasks by project
    //   GSI1PK=USER#{userId},       GSI1SK=TIMELOG#{date}    → Timelogs by user
    //   GSI1PK=TASK#{taskId},       GSI1SK=TIMELOG#{id}      → Timelogs for task
    //
    // GSI2 — Status-based queries and date-range queries:
    //   GSI2PK=STATUS#{status},     GSI2SK=TASK#{taskId}     → Tasks by status
    //   GSI2PK=DATE#{YYYY-MM-DD},   GSI2SK=TIMELOG#{id}      → Timelogs by date
    //   GSI2PK=TYPE#{entityType},    GSI2SK=CREATED#{date}    → Items by type+date

    const gsiDefinitions: GsiDefinition[] = [
      {
        indexName: 'GSI1',
        partitionKey: {
          name: 'GSI1PK',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'GSI1SK',
          type: dynamodb.AttributeType.STRING,
        },
      },
      {
        indexName: 'GSI2',
        partitionKey: {
          name: 'GSI2PK',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'GSI2SK',
          type: dynamodb.AttributeType.STRING,
        },
      },
      {
        indexName: 'GSI3',
        partitionKey: {
          name: 'GSI3PK',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'GSI3SK',
          type: dynamodb.AttributeType.STRING,
        },
      },
    ];

    const inventoryTable = new WebVellaDynamoDBTable(this, 'InventoryTable', {
      serviceName: 'erp-inventory',
      tableName: 'main',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the inventory table and its GSIs.
    // Covers all single-table access patterns for task, timelog, product,
    // and stock entities. Both the TaskHandler and TimelogHandler Lambda
    // functions require full CRUD access to the shared inventory table.
    const dynamoDbPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'dynamodb:GetItem',
        'dynamodb:PutItem',
        'dynamodb:UpdateItem',
        'dynamodb:DeleteItem',
        'dynamodb:Query',
        'dynamodb:Scan',
        'dynamodb:BatchGetItem',
        'dynamodb:BatchWriteItem',
      ],
      resources: [
        inventoryTable.tableArn,
        `${inventoryTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // Both Lambda functions publish domain events following the naming
    // convention from AAP §0.8.5: `inventory.{entity}.{action}`.
    // This replaces the monolith's synchronous HookManager post-hook
    // invocations for task/timelog lifecycle events.
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sns:Publish',
      ],
      resources: [
        eventBus.topicArn,
      ],
    });

    // -----------------------------------------------------------------------
    // 3. Lambda Functions — .NET 9 Native AOT handlers
    // -----------------------------------------------------------------------

    // 3a. TaskHandler — Task CRUD, comment operations, feed management
    //
    // Handles HTTP endpoints:
    //   POST   /v1/inventory/tasks             → Create task
    //   GET    /v1/inventory/tasks             → List tasks (with filtering)
    //   GET    /v1/inventory/tasks/{taskId}    → Get task details
    //   PUT    /v1/inventory/tasks/{taskId}    → Update task
    //   DELETE /v1/inventory/tasks/{taskId}    → Delete task
    //   POST   /v1/inventory/tasks/{taskId}/comments → Add comment
    //   GET    /v1/inventory/tasks/{taskId}/comments → List comments
    //   GET    /v1/inventory/tasks/{taskId}/feed     → Get task feed
    //   GET    /v1/inventory/tasks/statuses          → Get task statuses
    //
    // Source mapping:
    //   ProjectPlugin.cs     → Task entity schema seeding (fields: subject,
    //                          number, key, type, status, project relations)
    //   TaskService.cs       → SetCalculationFields (project abbreviation +
    //                          task number key generation), GetTaskStatuses(),
    //                          GetTask(), task lifecycle management
    //   ProjectController.cs → HTTP endpoint routing, auth, request/response
    //
    // Publishes domain events:
    //   inventory.task.created — after successful task creation
    //   inventory.task.updated — after successful task update (including
    //                            status changes, field edits, reassignment)
    //   inventory.task.deleted — after successful task deletion

    const taskHandler = new WebVellaLambdaService(this, 'TaskHandler', {
      serviceName: 'erp-inventory',
      functionName: 'task',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/inventory/publish',
      handler: 'WebVellaErp.Inventory::WebVellaErp.Inventory.Functions.TaskHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Inventory TaskHandler — task CRUD, comments, feed management. ' +
        'Replaces ProjectPlugin.cs + TaskService.cs business logic. ' +
        'Publishes inventory.task.{created,updated,deleted} events.',
      environment: {
        TABLE_NAME: inventoryTable.tableName,
        DYNAMODB_TABLE_NAME: inventoryTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // 3b. TimelogHandler — Timelog CRUD operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/inventory/timelogs             → Create timelog entry
    //   GET    /v1/inventory/timelogs             → List timelogs (filterable)
    //   GET    /v1/inventory/timelogs/{timelogId} → Get timelog details
    //   PUT    /v1/inventory/timelogs/{timelogId} → Update timelog entry
    //   DELETE /v1/inventory/timelogs/{timelogId} → Delete timelog entry
    //   GET    /v1/inventory/timelogs/by-task/{taskId} → Timelogs for task
    //   GET    /v1/inventory/timelogs/by-user/{userId} → Timelogs by user
    //
    // Source mapping:
    //   TimelogService.cs     → Timelog CRUD, billable hour tracking,
    //                            user/task association, date range queries
    //   ProjectController.cs  → HTTP endpoint routing, auth, request/response
    //
    // Publishes domain events:
    //   inventory.timelog.created — after successful timelog creation
    //   inventory.timelog.updated — after successful timelog update

    const timelogHandler = new WebVellaLambdaService(this, 'TimelogHandler', {
      serviceName: 'erp-inventory',
      functionName: 'timelog',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/inventory/publish',
      handler: 'WebVellaErp.Inventory::WebVellaErp.Inventory.Functions.TimelogHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Inventory TimelogHandler — timelog CRUD operations. Replaces ' +
        'TimelogService.cs business logic (billable hours, user/task association). ' +
        'Publishes inventory.timelog.{created,updated} domain events.',
      environment: {
        TABLE_NAME: inventoryTable.tableName,
        DYNAMODB_TABLE_NAME: inventoryTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // -----------------------------------------------------------------------
    // 4. SSM Parameter — Table name for cross-service discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use this parameter to locate
    // the inventory service's DynamoDB table without hardcoded names.
    // The seed-test-data.sh and run-migrations.sh scripts read this
    // parameter to configure test data insertion.

    const tableNameParam = new ssm.StringParameter(this, 'InventoryTableNameParam', {
      parameterName: '/webvella-erp/inventory/table-name',
      stringValue: inventoryTable.tableName,
      description:
        'DynamoDB table name for the Inventory / Project Management service ' +
        'datastore. Used by bootstrap scripts and cross-service discovery.',
    });

    // Apply conditional removal policy to SSM parameter for clean teardown
    // in LocalStack mode per AAP §0.7.6 dual-target strategy.
    tableNameParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    // -----------------------------------------------------------------------
    // 5. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [taskHandler.function, timelogHandler.function];
    this.tableName = inventoryTable.tableName;

    // -----------------------------------------------------------------------
    // 6. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------
    // These outputs are consumed by ApiGatewayStack for route integration
    // and by the CI/CD pipeline for deployment verification.

    new cdk.CfnOutput(this, 'InventoryTableName', {
      value: inventoryTable.tableName,
      description: 'DynamoDB table name for the Inventory service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'InventoryTableArn', {
      value: inventoryTable.tableArn,
      description: 'DynamoDB table ARN for the Inventory service',
      exportName: `${this.stackName}-TableArn`,
    });

    new cdk.CfnOutput(this, 'TaskHandlerFunctionArn', {
      value: taskHandler.functionArn,
      description: 'ARN of the Inventory TaskHandler Lambda function',
      exportName: `${this.stackName}-TaskHandlerArn`,
    });

    new cdk.CfnOutput(this, 'TaskHandlerFunctionName', {
      value: taskHandler.functionName,
      description: 'Name of the Inventory TaskHandler Lambda function',
      exportName: `${this.stackName}-TaskHandlerName`,
    });

    new cdk.CfnOutput(this, 'TimelogHandlerFunctionArn', {
      value: timelogHandler.functionArn,
      description: 'ARN of the Inventory TimelogHandler Lambda function',
      exportName: `${this.stackName}-TimelogHandlerArn`,
    });

    new cdk.CfnOutput(this, 'TimelogHandlerFunctionName', {
      value: timelogHandler.functionName,
      description: 'Name of the Inventory TimelogHandler Lambda function',
      exportName: `${this.stackName}-TimelogHandlerName`,
    });

    new cdk.CfnOutput(this, 'FunctionCount', {
      value: String(this.functions.length),
      description: 'Number of Lambda functions in the Inventory stack',
      exportName: `${this.stackName}-FunctionCount`,
    });

    // -----------------------------------------------------------------------
    // 7. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------
    // Tags applied at the stack level propagate to all child resources,
    // enabling cost allocation, operational visibility, and automated
    // discovery across the WebVella ERP microservices fleet.

    cdk.Tags.of(this).add('service', 'inventory');
    cdk.Tags.of(this).add('domain', 'inventory');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
