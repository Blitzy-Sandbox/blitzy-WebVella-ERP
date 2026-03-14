/**
 * WorkflowStack — Workflow Engine Service Infrastructure (Step Functions).
 *
 * This CDK stack defines all AWS resources for the Workflow Engine bounded
 * context, replacing the monolith's in-process `JobManager`/`JobPool`
 * 20-thread pool with AWS Step Functions for workflow orchestration and
 * Lambda functions for individual step execution.
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`workflow-workflow`) — Single-table design storing all
 *    workflow, job, and schedule metadata. Partition key patterns:
 *    - `WORKFLOW#{workflowId}` — Workflow definitions and orchestration metadata
 *    - `JOB#{jobId}` — Individual job records (migrated from PostgreSQL `jobs` table)
 *    - `SCHEDULE#{scheduleId}` — Schedule plans (migrated from `schedule_plans` table)
 *    Sort key patterns: `META`, `EXECUTION#{executionId}` for execution history.
 *    GSI1: Status-based lookups (pending jobs, running workflows).
 *    GSI2: Schedule-based lookups (next trigger time ordering).
 *
 * 2. **Step Functions State Machine** (`webvella-erp-workflow`) — STANDARD type
 *    state machine orchestrating multi-step workflow execution. Replaces the
 *    monolith's `JobManager.cs` dispatcher loop and `JobPool.cs` 20-thread
 *    bounded executor with serverless Step Functions orchestration. Supports
 *    approval chains and cross-domain Saga-pattern workflows per AAP §0.4.2.
 *    Definition is built programmatically via CDK constructs with an override
 *    path for custom ASL files in `services/workflow/src/StateMachines/`.
 *
 * 3. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    a. **WorkflowHandler** (`webvella-workflow-handler`) — 512 MB, 30s timeout.
 *       Handles workflow initiation, status queries, and schedule plan CRUD.
 *       Source: `JobManager.cs` dispatcher + `SheduleManager.cs` schedule CRUD.
 *    b. **StepHandler** (`webvella-workflow-step`) — 512 MB, 300s timeout.
 *       Handles individual Step Function step execution (task activities).
 *       Source: `JobPool.cs` worker execution + `ErpJob` subclass logic.
 *
 * 4. **EventBridge Rules** — Cron/rate-based rules triggering the WorkflowHandler
 *    for scheduled job processing, replacing `SheduleManager.cs` polling loop.
 *
 * 5. **SSM Parameters** — Table name and state machine ARN for cross-service
 *    discovery per AAP §0.8.6.
 *
 * Domain events published to the shared SNS event bus:
 * - `workflow.job.started` — Job execution initiated
 * - `workflow.job.completed` — Job execution completed successfully
 * - `workflow.job.failed` — Job execution failed
 * - `workflow.schedule.triggered` — Schedule plan triggered
 *
 * Source files referenced:
 * - WebVella.Erp/Jobs/JobManager.cs — Singleton job coordinator and dispatcher
 * - WebVella.Erp/Jobs/JobPool.cs — 20-thread bounded executor
 * - WebVella.Erp/Jobs/JobDataService.cs — PostgreSQL job/schedule persistence
 * - WebVella.Erp/Jobs/SheduleManager.cs — Schedule plan orchestrator
 * - WebVella.Erp/Jobs/ErpBackgroundServices.cs — BackgroundService adapters
 *
 * @module infra/src/stacks/workflow-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as sfn from 'aws-cdk-lib/aws-stepfunctions';
import * as tasks from 'aws-cdk-lib/aws-stepfunctions-tasks';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as fs from 'fs';
import * as path from 'path';

import {
  WebVellaLambdaService,
  LambdaRuntime,
  WebVellaDynamoDBTable,
  GsiDefinition,
} from '../constructs';

// ---------------------------------------------------------------------------
// Interface: WorkflowStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the WorkflowStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface WorkflowStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - Removal policies: DESTROY (LocalStack) vs RETAIN (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   * - Step Functions Local sidecar usage (docker-compose.yml, not in CDK)
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The WorkflowHandler Lambda publishes domain
   * events to this topic using the naming convention from AAP §0.8.5:
   * - `workflow.job.started`
   * - `workflow.job.completed`
   * - `workflow.job.failed`
   * - `workflow.schedule.triggered`
   *
   * Replaces the monolith's synchronous HookManager post-hook invocations
   * and PostgreSQL LISTEN/NOTIFY for job lifecycle events.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: WorkflowStack
// ---------------------------------------------------------------------------

/**
 * WorkflowStack — CDK stack for the Workflow Engine bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * table, Lambda functions, Step Functions state machine, EventBridge rules,
 * IAM policies, and SSM parameters. No other service may directly access the
 * workflow engine's datastore.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack and
 * other stacks for cross-stack integration:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 * - `stateMachineArn` — Step Functions ARN for workflow orchestration
 *
 * Performance targets (AAP §0.8.2):
 * - Lambda cold start (.NET Native AOT): < 1 second
 * - Step Functions workflow completion: < 30 seconds (standard approval chains)
 * - API response P95 (warm): < 500 ms
 */
export class WorkflowStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the WorkflowHandler and StepHandler functions. Consumed by
   * ApiGatewayStack for path-based routing under `/v1/workflow/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the workflow engine datastore.
   *
   * Follows the naming pattern: `workflow-workflow` (generated by
   * WebVellaDynamoDBTable as `{serviceName}-{tableName}`).
   * Also published as SSM parameter at `/webvella-erp/workflow/table-name`.
   */
  public readonly tableName: string;

  /**
   * ARN of the Step Functions state machine for workflow orchestration.
   *
   * Published as SSM parameter at `/webvella-erp/workflow/state-machine-arn`
   * for cross-service discovery. Used by the WorkflowHandler Lambda to
   * start executions and by monitoring/observability tooling.
   */
  public readonly stateMachineArn: string;

  constructor(scope: Construct, id: string, props: WorkflowStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for workflow engine
    // -----------------------------------------------------------------------
    // Replaces PostgreSQL tables: jobs, schedule_plans
    // Source: JobDataService.cs job/schedule persistence
    //
    // Access patterns:
    //   PK=WORKFLOW#{workflowId}, SK=META             → Workflow definition
    //   PK=WORKFLOW#{workflowId}, SK=EXECUTION#{id}   → Execution history
    //   PK=JOB#{jobId},          SK=META              → Job record
    //   PK=JOB#{jobId},          SK=EXECUTION#{id}    → Job execution details
    //   PK=SCHEDULE#{scheduleId},SK=META              → Schedule plan definition
    //   PK=SCHEDULE#{scheduleId},SK=TRIGGER#{ts}      → Trigger history entries
    //
    // GSI1 — Status-based lookups (pending jobs, running workflows):
    //   GSI1PK=STATUS#{status}, GSI1SK=CREATED#{timestamp}
    //   Enables: GetPendingJobs(), GetRunningJobs() from JobDataService.cs
    //
    // GSI2 — Schedule-based lookups (next trigger time ordering):
    //   GSI2PK=SCHEDULE_ACTIVE, GSI2SK=NEXT_RUN#{isoTimestamp}
    //   Enables: SheduleManager.cs ready-for-execution schedule lookup

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
    ];

    const workflowTable = new WebVellaDynamoDBTable(this, 'WorkflowTable', {
      serviceName: 'workflow',
      tableName: 'workflow',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the workflow table and its GSIs.
    // Replaces the direct PostgreSQL connection used by JobDataService.cs.
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
        workflowTable.tableArn,
        `${workflowTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // Replaces in-process HookManager post-hook invocations for job lifecycle.
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
    // 3. Lambda Function — StepHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Created BEFORE the state machine because the state machine definition
    // references this handler for LambdaInvoke tasks.
    //
    // Handles individual Step Function step execution (task activities).
    // Replaces JobPool.cs 20-thread bounded executor — each step invocation
    // is a separate Lambda execution with up to 300s timeout for long-running
    // job processing (e.g., CSV import, report generation, email batch send).
    //
    // Source mapping:
    //   JobPool.cs → RunJobAsync() + Process() worker dispatch
    //   ErpJob subclasses → Individual step execution logic
    //   JobDataService.cs → Status update after step completion

    const stepHandlerEnvironment: Record<string, string> = {
      TABLE_NAME: workflowTable.tableName,
      DYNAMODB_TABLE_NAME: workflowTable.tableName,
      EVENT_TOPIC_ARN: eventBus.topicArn,
    };

    if (isLocalStack) {
      stepHandlerEnvironment['AWS_ENDPOINT_URL'] = 'http://172.17.0.1:4566';
    }

    const stepHandler = new WebVellaLambdaService(this, 'StepHandler', {
      serviceName: 'workflow',
      functionName: 'step',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/workflow/publish',
      handler: 'WebVellaErp.Workflow::WebVellaErp.Workflow.Functions.WorkflowHandler::HandleApiRequest',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 300,
      description:
        'Workflow step executor — individual Step Function step execution replacing ' +
        'JobPool.cs 20-thread bounded executor. Handles initialization, task processing, ' +
        'and finalization with 300s timeout.',
      environment: stepHandlerEnvironment,
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // -----------------------------------------------------------------------
    // 4. Step Functions State Machine — Workflow orchestration
    // -----------------------------------------------------------------------
    // Replaces JobManager.cs dispatcher loop + JobPool.cs thread pool with
    // a STANDARD type state machine for long-running workflows (up to 1 year).
    // Implements the Saga Pattern per AAP §0.4.2 for cross-domain workflows
    // (e.g., invoice creation → inventory update → notification).
    //
    // State machine flow:
    //   InitializeWorkflow → CheckInitResult
    //     ├─ READY → ProcessStep → CheckStepResult
    //     │    ├─ MORE_STEPS → WaitBetweenSteps → ProcessStep (loop)
    //     │    ├─ COMPLETED → FinalizeWorkflow → WorkflowCompleted
    //     │    └─ (error) → HandleFailure → WorkflowFailed
    //     └─ (error) → HandleFailure → WorkflowFailed

    // 4a. State machine execution role — IAM least-privilege
    const stateMachineRole = new iam.Role(this, 'WorkflowStateMachineRole', {
      roleName: isLocalStack ? undefined : 'webvella-erp-workflow-sfn-role',
      assumedBy: new iam.ServicePrincipal('states.amazonaws.com'),
      description:
        'Execution role for the Workflow Engine Step Functions state machine. ' +
        'Grants permission to invoke the StepHandler Lambda for step execution.',
      managedPolicies: isLocalStack
        ? []
        : [
            iam.ManagedPolicy.fromAwsManagedPolicyName(
              'service-role/AWSLambdaRole',
            ),
          ],
    });

    // Explicit Lambda invoke permission scoped to StepHandler only
    stateMachineRole.addToPolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.ALLOW,
        actions: ['lambda:InvokeFunction'],
        resources: [
          stepHandler.functionArn,
          `${stepHandler.functionArn}:*`,
        ],
      }),
    );

    // 4b. Define workflow steps programmatically using CDK constructs
    // This replaces the monolith's synchronous job processing pipeline:
    //   JobManager.ProcessJobsAsync() → JobPool.RunJobAsync() → ErpJob.Process()

    // Terminal states
    const workflowCompleted = new sfn.Pass(this, 'WorkflowCompleted', {
      result: sfn.Result.fromObject({ status: 'COMPLETED' }),
      comment: 'Terminal success state — workflow completed successfully.',
    });

    const workflowFailed = new sfn.Pass(this, 'WorkflowFailed', {
      result: sfn.Result.fromObject({ status: 'FAILED' }),
      comment: 'Terminal failure state — workflow execution failed.',
    });

    // Error handling step — invokes StepHandler with error context for cleanup,
    // status persistence, and domain event publishing (workflow.job.failed).
    const handleFailure = new tasks.LambdaInvoke(this, 'HandleFailure', {
      lambdaFunction: stepHandler.function,
      payload: sfn.TaskInput.fromObject({
        'action': 'handleFailure',
        'workflowId.$': '$.workflowId',
        'jobId.$': '$.jobId',
        'error.$': '$.error',
      }),
      payloadResponseOnly: true,
      resultPath: '$.errorResult',
      comment:
        'Error handler — persists failure status and publishes workflow.job.failed event.',
    });
    handleFailure.next(workflowFailed);

    // Finalize step — invokes StepHandler to complete the workflow, persist
    // final status, and publish workflow.job.completed domain event.
    const finalizeWorkflow = new tasks.LambdaInvoke(this, 'FinalizeWorkflow', {
      lambdaFunction: stepHandler.function,
      payload: sfn.TaskInput.fromObject({
        'action': 'finalize',
        'workflowId.$': '$.workflowId',
        'jobId.$': '$.jobId',
        'stepResult.$': '$.stepResult',
      }),
      payloadResponseOnly: true,
      resultPath: '$.finalResult',
      comment:
        'Finalize workflow — persist completion status and publish workflow.job.completed.',
    });
    finalizeWorkflow.addCatch(handleFailure, {
      errors: ['States.ALL'],
      resultPath: '$.error',
    });
    finalizeWorkflow.next(workflowCompleted);

    // Wait between steps — configurable delay for throttling and back-pressure.
    // Replaces JobPool.cs semaphore-based concurrency control with Step Functions
    // native wait states, ensuring controlled execution cadence.
    const waitBetweenSteps = new sfn.Wait(this, 'WaitBetweenSteps', {
      time: sfn.WaitTime.duration(cdk.Duration.seconds(1)),
      comment:
        'Pause between step executions for throttling — replaces JobPool concurrency control.',
    });

    // Process step — invokes StepHandler for individual task execution.
    // Each invocation replaces one thread in the monolith's JobPool executing
    // an ErpJob subclass Process() method.
    const processStep = new tasks.LambdaInvoke(this, 'ProcessStep', {
      lambdaFunction: stepHandler.function,
      payload: sfn.TaskInput.fromObject({
        'action': 'execute',
        'workflowId.$': '$.workflowId',
        'jobId.$': '$.jobId',
        'stepIndex.$': '$.stepIndex',
        'attributes.$': '$.attributes',
      }),
      payloadResponseOnly: true,
      resultPath: '$.stepResult',
      comment:
        'Execute workflow step — replaces JobPool.cs worker thread execution.',
    });
    processStep.addCatch(handleFailure, {
      errors: ['States.ALL'],
      resultPath: '$.error',
    });

    // Choice: evaluate step result to determine next action
    const checkStepResult = new sfn.Choice(
      this,
      'CheckStepResult',
      {
        comment:
          'Evaluate step result: continue to next step, finalize, or handle failure.',
      },
    )
      .when(
        sfn.Condition.stringEquals('$.stepResult.status', 'MORE_STEPS'),
        waitBetweenSteps.next(processStep),
      )
      .when(
        sfn.Condition.stringEquals('$.stepResult.status', 'COMPLETED'),
        finalizeWorkflow,
      )
      .otherwise(handleFailure);

    processStep.next(checkStepResult);

    // Initialize step — invokes StepHandler to validate the workflow definition,
    // allocate resources, persist initial status, and publish workflow.job.started.
    const initializeWorkflow = new tasks.LambdaInvoke(
      this,
      'InitializeWorkflow',
      {
        lambdaFunction: stepHandler.function,
        payload: sfn.TaskInput.fromObject({
          'action': 'initialize',
          'workflowId.$': '$.workflowId',
          'jobId.$': '$.jobId',
          'typeId.$': '$.typeId',
          'typeName.$': '$.typeName',
          'priority.$': '$.priority',
          'attributes.$': '$.attributes',
        }),
        payloadResponseOnly: true,
        resultPath: '$.initResult',
        comment:
          'Initialize workflow — validate definition, allocate resources, publish ' +
          'workflow.job.started event. Replaces JobManager.cs job initialization.',
      },
    );
    initializeWorkflow.addCatch(handleFailure, {
      errors: ['States.ALL'],
      resultPath: '$.error',
    });

    // Choice: evaluate initialization result
    const checkInitResult = new sfn.Choice(this, 'CheckInitResult', {
      comment:
        'Evaluate initialization result: proceed to step execution or handle failure.',
    })
      .when(
        sfn.Condition.stringEquals('$.initResult.status', 'READY'),
        processStep,
      )
      .otherwise(handleFailure);

    // Build the complete workflow chain
    const programmaticDefinition = initializeWorkflow.next(checkInitResult);

    // 4c. State machine definition — supports file-based ASL override
    // Custom ASL files in services/workflow/src/StateMachines/ take precedence
    // over the programmatic definition when present at CDK synthesis time.
    // This enables advanced workflow patterns (parallel execution, map states,
    // complex retry/catch logic) without modifying the CDK stack.
    const customAslPath = path.resolve(
      __dirname,
      '../../../services/workflow/src/StateMachines/generic-workflow.asl.json',
    );

    let definitionBody: sfn.DefinitionBody;
    try {
      if (fs.existsSync(customAslPath)) {
        definitionBody = sfn.DefinitionBody.fromFile(customAslPath);
      } else {
        definitionBody = sfn.DefinitionBody.fromChainable(
          programmaticDefinition,
        );
      }
    } catch {
      // Fallback to programmatic definition if file read fails
      definitionBody = sfn.DefinitionBody.fromChainable(
        programmaticDefinition,
      );
    }

    // 4d. Create the state machine
    const stateMachine = new sfn.StateMachine(this, 'WorkflowStateMachine', {
      stateMachineName: 'webvella-erp-workflow',
      stateMachineType: sfn.StateMachineType.STANDARD,
      definitionBody,
      timeout: cdk.Duration.hours(24),
      role: stateMachineRole,
      tracingEnabled: !isLocalStack,
      comment:
        'WebVella ERP Workflow Engine — orchestrates multi-step workflow execution. ' +
        'Replaces JobManager/JobPool 20-thread pool with serverless Step Functions. ' +
        'Supports approval chains, Saga pattern, and long-running workflows.',
    });

    // Apply removal policy based on environment
    if (isLocalStack) {
      stateMachine.applyRemovalPolicy(cdk.RemovalPolicy.DESTROY);
    } else {
      stateMachine.applyRemovalPolicy(cdk.RemovalPolicy.RETAIN);
    }

    // -----------------------------------------------------------------------
    // 5. Lambda Function — WorkflowHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Created AFTER the state machine to reference its ARN in environment
    // variables and IAM policy without circular dependencies.
    //
    // Handles workflow initiation, status queries, and schedule plan CRUD:
    //   POST   /v1/workflow/jobs            → Start new job/workflow
    //   GET    /v1/workflow/jobs             → List jobs (with status filter)
    //   GET    /v1/workflow/jobs/{id}        → Get job status and details
    //   PUT    /v1/workflow/jobs/{id}/abort  → Abort running job
    //   POST   /v1/workflow/schedules       → Create schedule plan
    //   GET    /v1/workflow/schedules        → List schedule plans
    //   PUT    /v1/workflow/schedules/{id}   → Update schedule plan
    //   DELETE /v1/workflow/schedules/{id}   → Delete schedule plan
    //   POST   /v1/workflow/schedules/{id}/trigger → Trigger schedule now
    //
    // Source mapping:
    //   JobManager.cs → Job initiation, status queries, type registry
    //   SheduleManager.cs → Schedule plan CRUD, trigger management
    //   JobDataService.cs → Job/schedule persistence operations

    // Step Functions management permissions for WorkflowHandler
    const stepFunctionsPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'states:StartExecution',
        'states:DescribeExecution',
        'states:StopExecution',
        'states:ListExecutions',
      ],
      resources: [
        stateMachine.stateMachineArn,
        `${stateMachine.stateMachineArn}:*`,
      ],
    });

    const workflowHandlerEnvironment: Record<string, string> = {
      TABLE_NAME: workflowTable.tableName,
      DYNAMODB_TABLE_NAME: workflowTable.tableName,
      STATE_MACHINE_ARN: stateMachine.stateMachineArn,
      EVENT_TOPIC_ARN: eventBus.topicArn,
    };

    if (isLocalStack) {
      workflowHandlerEnvironment['AWS_ENDPOINT_URL'] = 'http://172.17.0.1:4566';
    }

    const workflowHandler = new WebVellaLambdaService(this, 'WorkflowHandler', {
      serviceName: 'workflow',
      functionName: 'handler',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/workflow/publish',
      handler: 'WebVellaErp.Workflow::WebVellaErp.Workflow.Functions.StepHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Workflow handler — workflow initiation, status queries, schedule plan CRUD, ' +
        'and Step Functions execution management. Replaces JobManager.cs dispatcher ' +
        'and SheduleManager.cs schedule orchestration.',
      environment: workflowHandlerEnvironment,
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy, stepFunctionsPolicy],
    });

    // -----------------------------------------------------------------------
    // 6. EventBridge Rules — Scheduled workflow triggers
    // -----------------------------------------------------------------------
    // Replaces the monolith's SheduleManager.cs polling loop that checked
    // for ready-for-execution schedule plans at configured intervals.
    // Two rules cover the two scheduling patterns from SheduleManager:
    //
    // a. Rate-based rule: processes pending schedules every minute.
    //    Replaces SheduleManager.IntervalInMinutes polling (default 1 min).
    //    Source: SheduleManager.cs Process() loop, SchedulePlanType.Interval.
    //
    // b. Cron-based rule: daily maintenance at 02:00 UTC for cleanup,
    //    stale job detection, and schedule plan reconciliation.
    //    Replaces SchedulePlanType.Daily pattern from SheduleManager.cs.

    const scheduledProcessingRule = new events.Rule(
      this,
      'WorkflowScheduleProcessingRule',
      {
        ruleName: 'webvella-erp-workflow-schedule-processing',
        schedule: events.Schedule.rate(cdk.Duration.minutes(1)),
        description:
          'Triggers WorkflowHandler every minute to process pending schedule plans. ' +
          'Replaces SheduleManager.cs IntervalInMinutes polling loop.',
        // Disabled in LocalStack to prevent retry storms when Lambda containers
        // take longer to cold-start than the 1-minute invocation interval.
        enabled: !props.isLocalStack,
      },
    );

    scheduledProcessingRule.addTarget(
      new targets.LambdaFunction(workflowHandler.function, {
        event: events.RuleTargetInput.fromObject({
          source: 'eventbridge.schedule',
          action: 'processSchedules',
          timestamp: events.EventField.time,
        }),
      }),
    );

    const dailyMaintenanceRule = new events.Rule(
      this,
      'WorkflowDailyMaintenanceRule',
      {
        ruleName: 'webvella-erp-workflow-daily-maintenance',
        schedule: events.Schedule.cron({
          minute: '0',
          hour: '2',
        }),
        description:
          'Daily maintenance at 02:00 UTC — cleans up stale executions, reconciles ' +
          'schedule plans, and archives completed workflow history. ' +
          'Replaces SchedulePlanType.Daily from SheduleManager.cs.',
        // Disabled in LocalStack to prevent retry storms during development.
        enabled: !props.isLocalStack,
      },
    );

    dailyMaintenanceRule.addTarget(
      new targets.LambdaFunction(workflowHandler.function, {
        event: events.RuleTargetInput.fromObject({
          source: 'eventbridge.schedule',
          action: 'dailyMaintenance',
          timestamp: events.EventField.time,
        }),
      }),
    );

    // -----------------------------------------------------------------------
    // 7. SSM Parameters — Cross-service discovery per AAP §0.8.6
    // -----------------------------------------------------------------------
    // Other services and bootstrap scripts use these parameters to locate
    // the workflow engine's DynamoDB table and Step Functions state machine
    // without hardcoded names or ARNs.

    new ssm.StringParameter(this, 'WorkflowTableNameParam', {
      parameterName: '/webvella-erp/workflow/table-name',
      stringValue: workflowTable.tableName,
      description:
        'DynamoDB table name for the Workflow Engine service datastore. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    new ssm.StringParameter(this, 'WorkflowStateMachineArnParam', {
      parameterName: '/webvella-erp/workflow/state-machine-arn',
      stringValue: stateMachine.stateMachineArn,
      description:
        'ARN of the Workflow Engine Step Functions state machine. ' +
        'Used by services that need to trigger or monitor workflow executions.',
    });

    // -----------------------------------------------------------------------
    // 8. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [workflowHandler.function, stepHandler.function];
    this.tableName = workflowTable.tableName;
    this.stateMachineArn = stateMachine.stateMachineArn;

    // -----------------------------------------------------------------------
    // 9. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'WorkflowTableName', {
      value: workflowTable.tableName,
      description: 'DynamoDB table name for the Workflow Engine service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'WorkflowHandlerFunctionArn', {
      value: workflowHandler.functionArn,
      description: 'ARN of the Workflow handler Lambda function',
      exportName: `${this.stackName}-WorkflowHandlerArn`,
    });

    new cdk.CfnOutput(this, 'StepHandlerFunctionArn', {
      value: stepHandler.functionArn,
      description: 'ARN of the Step handler Lambda function',
      exportName: `${this.stackName}-StepHandlerArn`,
    });

    new cdk.CfnOutput(this, 'WorkflowStateMachineArnOutput', {
      value: stateMachine.stateMachineArn,
      description: 'ARN of the Workflow Engine Step Functions state machine',
      exportName: `${this.stackName}-StateMachineArn`,
    });

    // -----------------------------------------------------------------------
    // 10. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', 'workflow');
    cdk.Tags.of(this).add('domain', 'workflow');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
