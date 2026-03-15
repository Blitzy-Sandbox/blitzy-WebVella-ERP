/**
 * PluginSystemStack — Plugin / Extension System Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the Plugin / Extension System
 * bounded context, replacing the monolith's reflection-based plugin discovery
 * (AppDomain.GetAssemblies → ErpPlugin subclass scanning) with a DynamoDB-backed
 * plugin registry that tracks registered plugins, their metadata, configuration,
 * versioned history, and associated app/sitemap/page definitions.
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`plugin-system-plugin-system`) — Single-table design
 *    storing all plugin metadata and configuration. Partition key patterns:
 *    - `PLUGIN#{pluginName}` — Plugin registration and metadata (Name, Prefix,
 *      Url, Description, Version, Company, Author from ErpPlugin.cs)
 *    - `APP#{appId}` — Application definitions (migrated from `app` table)
 *    - `SITEMAP#{sitemapId}` — Sitemap area/group/node definitions (from
 *      `app_sitemap_area`, `app_sitemap_group`, `app_sitemap_node` tables)
 *    - `PAGE#{pageId}` — Page definitions (from `app_page` table)
 *    Sort key patterns: `META`, `CONFIG`, `VERSION#{version}`
 *    GSI1: `GSI1PK`/`GSI1SK` for prefix-based lookups and type queries.
 *
 * 2. **Lambda Function** (`webvella-plugin-system-handler`) — .NET 9 Native AOT
 *    Lambda handling all plugin CRUD operations: registration, listing,
 *    configuration management, and app/sitemap/page metadata CRUD. Replaces
 *    `ErpPlugin.GetPluginData()`/`SavePluginData()` PostgreSQL persistence and
 *    `SdkPlugin.Initialize()` registration lifecycle.
 *
 * 3. **SSM Parameter** (`/webvella-erp/plugin-system/table-name`) — Stores the
 *    DynamoDB table name for cross-service discovery per AAP §0.8.6.
 *
 * Domain events published to the shared SNS event bus:
 * - `plugin-system.plugin.registered` — New plugin registered
 * - `plugin-system.plugin.updated` — Plugin configuration changed
 * - `plugin-system.app.created` — New application definition created
 *
 * Source files referenced:
 * - WebVella.Erp/ErpPlugin.cs — Abstract plugin base with JSON metadata persistence
 * - WebVella.Erp/IErpService.cs — Plugin initialization lifecycle contract
 * - WebVella.Erp.Plugins.SDK/SdkPlugin.cs — SDK admin console plugin registration
 * - WebVella.Erp.Plugins.MicrosoftCDM/MicrosoftCDMPlugin.cs — Plugin skeleton pattern
 *
 * @module infra/src/stacks/plugin-system-stack
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
// Interface: PluginSystemStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the PluginSystemStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface PluginSystemStackProps extends cdk.StackProps {
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
   * Passed from SharedStack. The PluginHandler Lambda publishes domain events
   * to this topic using the naming convention from AAP §0.8.5:
   * - `plugin-system.plugin.registered`
   * - `plugin-system.plugin.updated`
   * - `plugin-system.app.created`
   *
   * Replaces the monolith's synchronous HookManager post-hook invocations
   * for plugin lifecycle events.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: PluginSystemStack
// ---------------------------------------------------------------------------

/**
 * PluginSystemStack — CDK stack for the Plugin / Extension System bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB table,
 * Lambda functions, IAM policies, and SSM parameters. No other service may
 * directly access the plugin system's datastore.
 *
 * The stack exposes two public properties consumed by the ApiGatewayStack for
 * route-to-Lambda integration mapping:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 */
export class PluginSystemStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the PluginHandler function that handles all plugin system HTTP
   * endpoints. Consumed by ApiGatewayStack for path-based routing under
   * `/v1/plugin-system/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the plugin system datastore.
   *
   * Follows the naming pattern: `plugin-system-plugin-system` (generated by
   * WebVellaDynamoDBTable as `{serviceName}-{tableName}`).
   * Also published as SSM parameter at `/webvella-erp/plugin-system/table-name`.
   */
  public readonly tableName: string;

  constructor(scope: Construct, id: string, props: PluginSystemStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for plugin registry
    // -----------------------------------------------------------------------
    // Replaces PostgreSQL tables: plugin_data, app, app_page,
    // app_sitemap_area, app_sitemap_group, app_sitemap_node
    //
    // Access patterns:
    //   PK=PLUGIN#{pluginName}, SK=META          → Plugin registration/metadata
    //   PK=PLUGIN#{pluginName}, SK=CONFIG         → Plugin configuration JSON
    //   PK=PLUGIN#{pluginName}, SK=VERSION#{ver}  → Version history entries
    //   PK=APP#{appId},         SK=META           → Application definition
    //   PK=SITEMAP#{sitemapId}, SK=META           → Sitemap node definition
    //   PK=PAGE#{pageId},       SK=META           → Page definition
    //
    // GSI1 enables prefix-based lookups and cross-entity queries:
    //   GSI1PK=TYPE#PLUGIN,     GSI1SK=NAME#{name}  → List all plugins
    //   GSI1PK=TYPE#APP,        GSI1SK=NAME#{name}  → List all apps
    //   GSI1PK=PLUGIN#{name},   GSI1SK=APP#{appId}  → Apps for a plugin

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
    ];

    const pluginTable = new WebVellaDynamoDBTable(this, 'PluginTable', {
      serviceName: 'plugin-system',
      tableName: 'plugin-system',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the plugin system table and its GSIs
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
        pluginTable.tableArn,
        `${pluginTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic
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
    // 3. Lambda Function — PluginHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles all plugin system HTTP operations:
    //   POST   /v1/plugin-system/plugins          → Register plugin
    //   GET    /v1/plugin-system/plugins           → List plugins
    //   GET    /v1/plugin-system/plugins/{name}    → Get plugin details
    //   PUT    /v1/plugin-system/plugins/{name}    → Update plugin
    //   DELETE /v1/plugin-system/plugins/{name}    → Deregister plugin
    //   GET    /v1/plugin-system/plugins/{name}/config → Get configuration
    //   PUT    /v1/plugin-system/plugins/{name}/config → Update configuration
    //   POST   /v1/plugin-system/apps              → Create app definition
    //   GET    /v1/plugin-system/apps              → List apps
    //   POST   /v1/plugin-system/sitemaps          → Create sitemap node
    //   POST   /v1/plugin-system/pages             → Create page definition
    //
    // Source mapping:
    //   ErpPlugin.cs   → Plugin CRUD + metadata model
    //   SdkPlugin.cs   → Registration lifecycle pattern
    //   IErpService.cs → InitializePlugins() lifecycle

    const pluginHandler = new WebVellaLambdaService(this, 'PluginHandler', {
      serviceName: 'plugin-system',
      functionName: 'handler',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/plugin-system/publish',
      handler: 'WebVellaErp.PluginSystem::WebVellaErp.PluginSystem.Functions.PluginHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Plugin System handler — plugin registration, listing, configuration CRUD, ' +
        'and app/sitemap/page metadata management. Replaces ErpPlugin reflection-based ' +
        'discovery and plugin_data PostgreSQL persistence.',
      environment: {
        TABLE_NAME: pluginTable.tableName,
        PLUGIN_SYSTEM_TABLE_NAME: pluginTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // -----------------------------------------------------------------------
    // 4. SSM Parameter — Table name for cross-service discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use this parameter to locate
    // the plugin system's DynamoDB table without hardcoded names.

    new ssm.StringParameter(this, 'PluginTableNameParam', {
      parameterName: '/webvella-erp/plugin-system/table-name',
      stringValue: pluginTable.tableName,
      description:
        'DynamoDB table name for the Plugin System service datastore. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    // -----------------------------------------------------------------------
    // 5. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [pluginHandler.function];
    this.tableName = pluginTable.tableName;

    // -----------------------------------------------------------------------
    // 6. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'PluginSystemTableName', {
      value: pluginTable.tableName,
      description: 'DynamoDB table name for the Plugin System service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'PluginHandlerFunctionArn', {
      value: pluginHandler.functionArn,
      description: 'ARN of the Plugin System handler Lambda function',
      exportName: `${this.stackName}-HandlerArn`,
    });

    new cdk.CfnOutput(this, 'PluginHandlerFunctionName', {
      value: pluginHandler.functionName,
      description: 'Name of the Plugin System handler Lambda function',
      exportName: `${this.stackName}-HandlerName`,
    });

    // -----------------------------------------------------------------------
    // 7. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', 'plugin-system');
    cdk.Tags.of(this).add('domain', 'plugin-system');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
