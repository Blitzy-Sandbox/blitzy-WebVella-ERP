/**
 * CrmStack — CRM / Contacts Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the CRM / Contacts bounded
 * context, replacing the monolith's CRM plugin skeleton
 * (`WebVella.Erp.Plugins.Crm/CrmPlugin.cs`) and the Next plugin's entity
 * provisioning patches (`NextPlugin.20190204.cs`, `NextPlugin.20190206.cs`)
 * with a DynamoDB-backed serverless service handling account and contact
 * management, address association, and salutation lookups.
 *
 * **Source systems replaced:**
 * - `CrmPlugin.cs` — CRM plugin entry point (name, prefix, version metadata).
 * - `NextPlugin.20190204.cs` — Creates `account`, `contact`, `address` entity
 *   definitions with fields (type, name, email, phone, website, street, region,
 *   post_code, fixed_phone, created_on). Defines entity relations between
 *   contact→account. Account entity ID: `2e22b50f-e444-4b62-a171-076e51246939`.
 *   Contact entity ID: `39e1dd9b-827f-464d-95ea-507ade81cbd0`.
 * - `NextPlugin.20190206.cs` — Creates `salutation` entity, adds salutation
 *   fields to contact entity for formal address prefix support.
 * - `SearchService.cs` — `x_search` field regeneration for CRM entities:
 *   concatenates searchable fields into a single search index field.
 * - `Configuration.cs` — Search index field definitions:
 *   AccountSearchIndexFields (17 fields), ContactSearchIndexFields (15 fields).
 * - Post-create/update hooks from `Hooks/Api/` — Migrated to SNS domain
 *   events per AAP §0.7.2 hook-to-event migration strategy.
 *
 * **Target architecture:**
 * - DynamoDB table for all CRM data (single-table design)
 * - 2 Lambda functions for domain-specific CRUD operations
 * - SNS domain events for cross-service communication
 * - SSM Parameter Store for resource discovery
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`erp-crm-main`) — Single-table design storing all
 *    CRM entities. Partition key patterns:
 *    - `ACCOUNT#{accountId}` — Account records (company/person type, name,
 *      email, website, phone, address fields from NextPlugin.20190204.cs)
 *    - `CONTACT#{contactId}` — Contact records (name, email, phone, salutation
 *      from NextPlugin.20190204.cs + NextPlugin.20190206.cs)
 *    - `ADDRESS#{addressId}` — Address records (street, region, post_code)
 *    - `SALUTATION#{id}` — Salutation lookup records (prefix/label)
 *    Sort key patterns: `META` for main records, `RELATION#{type}` for
 *    relationship entries.
 *    GSI1: `GSI1PK`/`GSI1SK` — For email/name lookups across accounts and
 *    contacts (e.g., find by email, alphabetical name listing).
 *    GSI2: `GSI2PK`/`GSI2SK` — For account→contact relationship queries
 *    (e.g., all contacts for an account, all addresses for a contact).
 *
 * 2. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    - `webvella-erp-crm-account` (512 MB, 30s) — Account CRUD operations.
 *      Replaces NextPlugin.20190204.cs account entity definition and
 *      post-create/update hooks.
 *    - `webvella-erp-crm-contact` (512 MB, 30s) — Contact CRUD with
 *      salutation support. Replaces NextPlugin.20190204.cs +
 *      NextPlugin.20190206.cs contact/salutation entity definitions.
 *
 * 3. **SSM Parameter** (`/webvella-erp/crm/table-name`) — Stores the
 *    DynamoDB table name for cross-service discovery per AAP §0.8.6.
 *
 * Domain events published to the shared SNS event bus:
 * - `crm.account.created` — New account created
 * - `crm.account.updated` — Account updated (field edit, type change)
 * - `crm.account.deleted` — Account deleted
 * - `crm.contact.created` — New contact created
 * - `crm.contact.updated` — Contact updated (field edit, salutation change)
 * - `crm.contact.deleted` — Contact deleted
 *
 * Source files referenced:
 * - WebVella.Erp.Plugins.Crm/CrmPlugin.cs — CRM plugin entry point
 * - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs — Account/contact entities
 * - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs — Salutation entity
 * - WebVella.Erp.Plugins.Next/Services/SearchService.cs — Search indexing
 * - WebVella.Erp.Plugins.Next/Configuration.cs — Search field definitions
 *
 * @module infra/src/stacks/crm-stack
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
// Interface: CrmStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the CrmStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface CrmStackProps extends cdk.StackProps {
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
   * Passed from SharedStack. The AccountHandler and ContactHandler Lambda
   * functions publish domain events to this topic using the naming
   * convention from AAP §0.8.5:
   * - `crm.account.created`
   * - `crm.account.updated`
   * - `crm.account.deleted`
   * - `crm.contact.created`
   * - `crm.contact.updated`
   * - `crm.contact.deleted`
   *
   * Replaces the monolith's synchronous IErpPostCreateRecordHook and
   * IErpPostUpdateRecordHook invocations for account/contact entities
   * from `Hooks/Api/` with asynchronous SNS event publishing per
   * AAP §0.7.2 hook-to-event migration strategy.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: CrmStack
// ---------------------------------------------------------------------------

/**
 * CrmStack — CDK stack for the CRM / Contacts bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * table, Lambda functions, IAM policies, and SSM parameters. No other
 * service may directly access the CRM service's datastore.
 *
 * The stack exposes two public properties consumed by the ApiGatewayStack
 * for route-to-Lambda integration mapping:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 */
export class CrmStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the AccountHandler and ContactHandler functions that handle
   * all CRM service HTTP endpoints. Consumed by ApiGatewayStack for
   * path-based routing under `/v1/crm/*`.
   *
   * Index 0: AccountHandler — account CRUD operations
   * Index 1: ContactHandler — contact CRUD with salutation support
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the CRM service datastore.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/crm/table-name` for cross-service discovery.
   */
  public readonly tableName: string;

  constructor(scope: Construct, id: string, props: CrmStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for CRM / Contacts
    // -----------------------------------------------------------------------
    // Replaces PostgreSQL dynamic entity tables `rec_account`, `rec_contact`,
    // `rec_address`, `rec_salutation` from the monolith's Next plugin entity
    // provisioning (NextPlugin.20190204.cs, NextPlugin.20190206.cs).
    //
    // Single-table design access patterns:
    //
    //   PK=ACCOUNT#{accountId},     SK=META               → Account record
    //     Fields: type (Company/Person), name, email, website, phone,
    //     street, region, post_code, fixed_phone, created_on, x_search
    //     (Source: NextPlugin.20190204.cs account entity definition,
    //      entity ID 2e22b50f-e444-4b62-a171-076e51246939)
    //
    //   PK=ACCOUNT#{accountId},     SK=RELATION#{type}    → Account relations
    //     e.g., RELATION#CONTACT#{contactId} for account→contact links
    //
    //   PK=CONTACT#{contactId},     SK=META               → Contact record
    //     Fields: name, email, phone, salutation, account reference,
    //     created_on, x_search
    //     (Source: NextPlugin.20190204.cs + NextPlugin.20190206.cs,
    //      entity ID 39e1dd9b-827f-464d-95ea-507ade81cbd0)
    //
    //   PK=CONTACT#{contactId},     SK=RELATION#{type}    → Contact relations
    //
    //   PK=ADDRESS#{addressId},     SK=META               → Address record
    //     Fields: street, city, region, post_code, country
    //
    //   PK=SALUTATION#{id},         SK=META               → Salutation lookup
    //     Fields: label, abbreviation, sort_order
    //     (Source: NextPlugin.20190206.cs salutation entity definition)
    //
    // GSI1 — Email/name lookups across accounts and contacts:
    //   GSI1PK=EMAIL#{email},       GSI1SK=ENTITY#{type}#{id} → Find by email
    //   GSI1PK=NAME#{sortableName}, GSI1SK=ENTITY#{type}#{id} → Name listing
    //   Enables search patterns from Configuration.cs AccountSearchIndexFields
    //   (17 fields) and ContactSearchIndexFields (15 fields) that powered
    //   SearchService.cs x_search field regeneration.
    //
    // GSI2 — Account→contact relationship queries:
    //   GSI2PK=ACCOUNT#{accountId}, GSI2SK=CONTACT#{contactId} → Contacts list
    //   GSI2PK=TYPE#{entityType},   GSI2SK=CREATED#{isoDate}  → By type+date
    //   Replaces the monolith's entity_relations join table pattern for the
    //   contact→account N:1 relation defined in NextPlugin.20190204.cs.

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

    const crmTable = new WebVellaDynamoDBTable(this, 'CrmTable', {
      serviceName: 'erp-crm',
      tableName: 'main',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the CRM table and its GSIs.
    // Covers all single-table access patterns for account, contact, address,
    // and salutation entities. Both the AccountHandler and ContactHandler
    // Lambda functions require full CRUD access to the shared CRM table.
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
        crmTable.tableArn,
        `${crmTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // Both Lambda functions publish domain events following the naming
    // convention from AAP §0.8.5: `crm.{entity}.{action}`.
    // This replaces the monolith's synchronous HookManager post-hook
    // invocations for account/contact lifecycle events (IErpPostCreate-
    // RecordHook, IErpPostUpdateRecordHook, IErpPostDeleteRecordHook)
    // from WebVella.Erp.Plugins.Next/Hooks/Api/*.
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

    // 3a. AccountHandler — Account CRUD operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/crm/accounts             → Create account
    //   GET    /v1/crm/accounts             → List accounts (with filtering)
    //   GET    /v1/crm/accounts/{accountId} → Get account details
    //   PUT    /v1/crm/accounts/{accountId} → Update account
    //   DELETE /v1/crm/accounts/{accountId} → Delete account
    //   GET    /v1/crm/accounts/{accountId}/contacts → Contacts for account
    //   GET    /v1/crm/accounts/search      → Search accounts by name/email
    //
    // Source mapping:
    //   NextPlugin.20190204.cs → Account entity definition: type enum
    //     (Company/Person), name, email, website, phone, street, region,
    //     post_code, fixed_phone, created_on. Entity ID:
    //     2e22b50f-e444-4b62-a171-076e51246939.
    //   Configuration.cs → AccountSearchIndexFields (17 searchable fields)
    //     used to populate x_search via SearchService.cs.
    //   Hooks/Api/ → Post-create/update hooks migrated to SNS events.
    //
    // Publishes domain events:
    //   crm.account.created — after successful account creation
    //   crm.account.updated — after successful account update (including
    //                          type change, field edits, address updates)
    //   crm.account.deleted — after successful account deletion

    const accountHandler = new WebVellaLambdaService(this, 'AccountHandler', {
      serviceName: 'erp-crm',
      functionName: 'account',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/crm/publish',
      handler: 'WebVellaErp.Crm::WebVellaErp.Crm.Functions.AccountHandler::HandleAsync',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'CRM AccountHandler — account CRUD operations. Replaces ' +
        'NextPlugin.20190204.cs account entity definition (type, name, email, ' +
        'phone, website, address fields). Publishes crm.account.{created,' +
        'updated,deleted} domain events to SNS event bus.',
      environment: {
        TABLE_NAME: crmTable.tableName,
        'DynamoDB__CrmTableName': crmTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // 3b. ContactHandler — Contact CRUD with salutation support
    //
    // Handles HTTP endpoints:
    //   POST   /v1/crm/contacts             → Create contact
    //   GET    /v1/crm/contacts             → List contacts (with filtering)
    //   GET    /v1/crm/contacts/{contactId} → Get contact details
    //   PUT    /v1/crm/contacts/{contactId} → Update contact
    //   DELETE /v1/crm/contacts/{contactId} → Delete contact
    //   GET    /v1/crm/contacts/search      → Search contacts by name/email
    //   GET    /v1/crm/salutations          → List salutation options
    //
    // Source mapping:
    //   NextPlugin.20190204.cs → Contact entity definition: name, email,
    //     phone, account reference. Entity ID:
    //     39e1dd9b-827f-464d-95ea-507ade81cbd0.
    //   NextPlugin.20190206.cs → Salutation entity definition: label,
    //     abbreviation. Contact entity salutation field addition.
    //   Configuration.cs → ContactSearchIndexFields (15 searchable fields)
    //     used to populate x_search via SearchService.cs.
    //   Hooks/Api/ → Post-create/update hooks migrated to SNS events.
    //
    // Publishes domain events:
    //   crm.contact.created — after successful contact creation
    //   crm.contact.updated — after successful contact update (including
    //                          salutation change, account reassignment)
    //   crm.contact.deleted — after successful contact deletion

    const contactHandler = new WebVellaLambdaService(this, 'ContactHandler', {
      serviceName: 'erp-crm',
      functionName: 'contact',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/crm/publish',
      handler: 'WebVellaErp.Crm::WebVellaErp.Crm.Functions.ContactHandler::HandleAsync',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'CRM ContactHandler — contact CRUD with salutation support. Replaces ' +
        'NextPlugin.20190204.cs + NextPlugin.20190206.cs contact/salutation ' +
        'entity definitions. Publishes crm.contact.{created,updated,deleted} ' +
        'domain events to SNS event bus.',
      environment: {
        TABLE_NAME: crmTable.tableName,
        'DynamoDB__CrmTableName': crmTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // -----------------------------------------------------------------------
    // 4. SSM Parameter — Table name for cross-service discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use this parameter to locate
    // the CRM service's DynamoDB table without hardcoded names.
    // The seed-test-data.sh and run-migrations.sh scripts read this
    // parameter to configure test data insertion.

    const tableNameParam = new ssm.StringParameter(this, 'CrmTableNameParam', {
      parameterName: '/webvella-erp/crm/table-name',
      stringValue: crmTable.tableName,
      description:
        'DynamoDB table name for the CRM / Contacts service datastore. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    // Apply conditional removal policy to SSM parameter for clean teardown
    // in LocalStack mode per AAP §0.7.6 dual-target strategy.
    tableNameParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    // -----------------------------------------------------------------------
    // 5. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [accountHandler.function, contactHandler.function];
    this.tableName = crmTable.tableName;

    // -----------------------------------------------------------------------
    // 6. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------
    // These outputs are consumed by ApiGatewayStack for route integration
    // and by the CI/CD pipeline for deployment verification.

    new cdk.CfnOutput(this, 'CrmTableName', {
      value: crmTable.tableName,
      description: 'DynamoDB table name for the CRM service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'CrmTableArn', {
      value: crmTable.tableArn,
      description: 'DynamoDB table ARN for the CRM service',
      exportName: `${this.stackName}-TableArn`,
    });

    new cdk.CfnOutput(this, 'AccountHandlerFunctionArn', {
      value: accountHandler.functionArn,
      description: 'ARN of the CRM AccountHandler Lambda function',
      exportName: `${this.stackName}-AccountHandlerArn`,
    });

    new cdk.CfnOutput(this, 'AccountHandlerFunctionName', {
      value: accountHandler.functionName,
      description: 'Name of the CRM AccountHandler Lambda function',
      exportName: `${this.stackName}-AccountHandlerName`,
    });

    new cdk.CfnOutput(this, 'ContactHandlerFunctionArn', {
      value: contactHandler.functionArn,
      description: 'ARN of the CRM ContactHandler Lambda function',
      exportName: `${this.stackName}-ContactHandlerArn`,
    });

    new cdk.CfnOutput(this, 'ContactHandlerFunctionName', {
      value: contactHandler.functionName,
      description: 'Name of the CRM ContactHandler Lambda function',
      exportName: `${this.stackName}-ContactHandlerName`,
    });

    new cdk.CfnOutput(this, 'FunctionCount', {
      value: String(this.functions.length),
      description: 'Number of Lambda functions in the CRM stack',
      exportName: `${this.stackName}-FunctionCount`,
    });

    // -----------------------------------------------------------------------
    // 7. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------
    // Tags applied at the stack level propagate to all child resources,
    // enabling cost allocation, operational visibility, and automated
    // discovery across the WebVella ERP microservices fleet.

    cdk.Tags.of(this).add('service', 'crm');
    cdk.Tags.of(this).add('domain', 'crm');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
