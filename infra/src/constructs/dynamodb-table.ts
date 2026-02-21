/**
 * infra/src/constructs/dynamodb-table.ts
 *
 * Standard DynamoDB Table CDK L3 Construct for WebVella ERP Serverless Platform.
 *
 * Replaces the PostgreSQL table/index/constraint creation patterns from the
 * monolith's DbRepository.cs and DbEntityRepository.cs with a CDK-based
 * DynamoDB single-table design construct. Each bounded-context service
 * (Identity, Entity Management, CRM, Inventory, Notifications, File Management,
 * Workflow, Plugin System) uses this construct to create its own DynamoDB table.
 *
 * Single-Table Design:
 *   - Default PK/SK pattern: PK (STRING) / SK (STRING)
 *   - Entity Management: PK=ENTITY#{entityId}, SK=META|FIELD#{fieldId}|RELATION#{relationId}
 *   - Record storage: PK=ENTITY#{entityName}, SK=RECORD#{recordId}
 *   - Other services follow similar composite key patterns
 *
 * Key architecture rules enforced:
 *   - Database-Per-Service (AAP §0.4.2): each service gets its own table
 *   - Encryption at rest (AAP §0.8.3): AWS managed key by default
 *   - Dual-target (AAP §0.7.6): DESTROY for LocalStack, RETAIN for production
 *   - DynamoDB read latency P99 < 10ms (AAP §0.8.2): PAY_PER_REQUEST billing
 *   - Single entity ownership (AAP §0.8.1): one service per table
 *
 * @module infra/src/constructs/dynamodb-table
 */

import * as cdk from 'aws-cdk-lib';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import { Construct } from 'constructs';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Definition for a Global Secondary Index (GSI) to be added to a DynamoDB table.
 *
 * GSIs enable access patterns beyond the base table's PK/SK. For example,
 * the Entity Management service uses a GSI on `system_search` for full-text
 * search indexing (replacing the monolith's PostgreSQL FTS).
 */
export interface GsiDefinition {
  /**
   * GSI index name.
   * Convention: `{purpose}-index` (e.g., 'gsi1-index', 'entity-type-index', 'search-index').
   */
  indexName: string;

  /**
   * Partition key attribute for the GSI.
   * Attribute name and DynamoDB type (typically STRING for composite keys).
   */
  partitionKey: {
    name: string;
    type: dynamodb.AttributeType;
  };

  /**
   * Optional sort key attribute for the GSI.
   * Enables range queries within the GSI partition.
   */
  sortKey?: {
    name: string;
    type: dynamodb.AttributeType;
  };

  /**
   * Projection type for the GSI.
   * - ALL: all attributes projected (default — largest storage, most flexible)
   * - KEYS_ONLY: only key attributes projected (smallest storage)
   * - INCLUDE: specific non-key attributes projected
   * Defaults to ALL for maximum query flexibility.
   */
  projectionType?: dynamodb.ProjectionType;

  /**
   * Non-key attributes to include in the GSI projection.
   * Only applicable when projectionType is INCLUDE.
   */
  nonKeyAttributes?: string[];
}

/**
 * Configuration properties for creating a standardized DynamoDB table
 * for a WebVella ERP bounded-context service.
 *
 * Implements the single-table design pattern: PK (STRING) / SK (STRING)
 * by default, allowing each service to encode its access patterns in
 * composite key values (e.g., PK=ENTITY#123, SK=FIELD#456).
 */
export interface WebVellaDynamoDBTableProps {
  /**
   * Service domain name that owns this table.
   * Must match the bounded-context name (e.g., 'identity', 'crm', 'entity-management',
   * 'inventory', 'notifications', 'file-management', 'workflow', 'plugin-system').
   * Used for table naming, tagging, and SSM parameter paths.
   */
  serviceName: string;

  /**
   * Table name suffix appended to the service name.
   * Full table name: `{serviceName}-{tableName}` (e.g., 'identity-main', 'crm-main',
   * 'entity-management-metadata', 'entity-management-records').
   * Convention: 'main' for single-table services, descriptive suffix for multi-table services.
   */
  tableName: string;

  /**
   * Partition key configuration for the base table.
   * Defaults to `{ name: 'PK', type: STRING }` for the single-table design pattern.
   * Override only when a service requires a different key schema (rare).
   */
  partitionKey?: {
    name: string;
    type: dynamodb.AttributeType;
  };

  /**
   * Sort key configuration for the base table.
   * Defaults to `{ name: 'SK', type: STRING }` for the single-table design pattern.
   * Override only when a service requires a different key schema (rare).
   * Set to undefined to create a table with partition key only (no sort key).
   */
  sortKey?: {
    name: string;
    type: dynamodb.AttributeType;
  };

  /**
   * DynamoDB billing mode.
   * Defaults to PAY_PER_REQUEST (on-demand) for the serverless pattern.
   * PAY_PER_REQUEST eliminates the need for capacity planning and supports
   * the P99 < 10ms read latency target from AAP §0.8.2.
   */
  billingMode?: dynamodb.BillingMode;

  /**
   * Global Secondary Index definitions to add to the table.
   * Each GSI enables an additional access pattern beyond the base table's PK/SK.
   * Example: Entity Management service adds a GSI for search indexing.
   */
  globalSecondaryIndexes?: GsiDefinition[];

  /**
   * Whether deploying to LocalStack (true) or production AWS (false).
   * Controls conditional behavior:
   * - Removal policy: DESTROY for LocalStack, RETAIN for production
   * - Point-in-time recovery: disabled for LocalStack, enabled for production
   * This implements the dual-target CDK strategy from AAP §0.7.6.
   */
  isLocalStack: boolean;

  /**
   * Removal policy for the DynamoDB table.
   * Defaults to DESTROY for LocalStack (clean teardown), RETAIN for production
   * (protect data). Can be explicitly overridden when needed.
   */
  removalPolicy?: cdk.RemovalPolicy;

  /**
   * Enable point-in-time recovery (PITR) for continuous backups.
   * Defaults to false for LocalStack (not needed), true for production (data safety).
   */
  pointInTimeRecovery?: boolean;

  /**
   * Encryption at rest configuration.
   * Defaults to AWS_MANAGED (AWS owned key) per AAP §0.8.3 encryption requirements.
   * Use CUSTOMER_MANAGED for stricter compliance needs (KMS CMK).
   */
  encryption?: dynamodb.TableEncryption;

  /**
   * TTL attribute name for automatic item expiration.
   * Optional — used for auto-expiry of transient data such as session tokens,
   * temporary upload records, or cached projections.
   * The attribute must contain a Unix epoch timestamp (number).
   */
  timeToLiveAttribute?: string;

  /**
   * Enable DynamoDB Streams for change data capture.
   * Optional — used for event sourcing patterns where downstream consumers
   * need to react to table changes (e.g., Entity Management publishing domain
   * events when records are created/updated/deleted).
   * Common value: NEW_AND_OLD_IMAGES for full before/after snapshots.
   */
  stream?: dynamodb.StreamViewType;
}

// ---------------------------------------------------------------------------
// Construct Implementation
// ---------------------------------------------------------------------------

/**
 * WebVellaDynamoDBTable — Reusable CDK L3 construct for standardized DynamoDB
 * table creation across all bounded-context services.
 *
 * This construct replaces the PostgreSQL DDL operations from the monolith's
 * DbRepository.CreateTable(), DbRepository.CreateColumn(), and
 * DbEntityRepository.Create() methods with declarative CDK infrastructure.
 *
 * Each of the 8 DynamoDB-backed services (Identity, Entity Management, CRM,
 * Inventory, Notifications, File Management, Workflow, Plugin System) uses
 * this construct to create its own DynamoDB table with:
 *   - Single-table design (PK/SK STRING keys)
 *   - PAY_PER_REQUEST billing (serverless)
 *   - Encryption at rest (AWS managed key)
 *   - Optional GSIs for additional access patterns
 *   - Optional TTL for automatic item expiration
 *   - Optional DynamoDB Streams for event sourcing
 *   - Conditional configuration based on LocalStack vs production
 *
 * Usage Example:
 * ```typescript
 * const table = new WebVellaDynamoDBTable(this, 'MainTable', {
 *   serviceName: 'entity-management',
 *   tableName: 'metadata',
 *   isLocalStack: false,
 *   stream: dynamodb.StreamViewType.NEW_AND_OLD_IMAGES,
 *   globalSecondaryIndexes: [{
 *     indexName: 'gsi1-index',
 *     partitionKey: { name: 'GSI1PK', type: dynamodb.AttributeType.STRING },
 *     sortKey: { name: 'GSI1SK', type: dynamodb.AttributeType.STRING },
 *   }],
 * });
 *
 * // Grant Lambda read/write access
 * table.table.grantReadWriteData(lambdaFunction);
 * ```
 */
export class WebVellaDynamoDBTable extends Construct {
  /**
   * The underlying CDK DynamoDB Table resource.
   * Use this for IAM grants (grantReadData, grantReadWriteData, grantFullAccess)
   * and for adding event source mappings in consuming stacks.
   */
  public readonly table: dynamodb.Table;

  /**
   * The full DynamoDB table name (e.g., 'entity-management-metadata').
   * Use for SSM Parameter Store entries and Lambda environment variables.
   */
  public readonly tableName: string;

  /**
   * The DynamoDB table ARN.
   * Use for cross-stack references and IAM policy resources.
   */
  public readonly tableArn: string;

  constructor(scope: Construct, id: string, props: WebVellaDynamoDBTableProps) {
    super(scope, id);

    // -----------------------------------------------------------------------
    // 1. Input validation
    // -----------------------------------------------------------------------
    if (!props.serviceName || props.serviceName.trim().length === 0) {
      throw new Error(
        'WebVellaDynamoDBTable: serviceName is required and must be a non-empty string.'
      );
    }
    if (!props.tableName || props.tableName.trim().length === 0) {
      throw new Error(
        'WebVellaDynamoDBTable: tableName is required and must be a non-empty string.'
      );
    }

    // Validate service name follows kebab-case convention
    const kebabCaseRegex = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/;
    if (!kebabCaseRegex.test(props.serviceName)) {
      throw new Error(
        `WebVellaDynamoDBTable: serviceName '${props.serviceName}' must be kebab-case ` +
          '(e.g., "identity", "entity-management", "file-management").'
      );
    }
    if (!kebabCaseRegex.test(props.tableName)) {
      throw new Error(
        `WebVellaDynamoDBTable: tableName '${props.tableName}' must be kebab-case ` +
          '(e.g., "main", "metadata", "records").'
      );
    }

    // -----------------------------------------------------------------------
    // 2. Determine defaults based on isLocalStack flag (AAP §0.7.6)
    // -----------------------------------------------------------------------

    // Removal policy: DESTROY for LocalStack (clean teardown), RETAIN for production
    const resolvedRemovalPolicy: cdk.RemovalPolicy =
      props.removalPolicy !== undefined
        ? props.removalPolicy
        : props.isLocalStack
          ? cdk.RemovalPolicy.DESTROY
          : cdk.RemovalPolicy.RETAIN;

    // Point-in-time recovery: disabled for LocalStack, enabled for production
    const resolvedPointInTimeRecoveryEnabled: boolean =
      props.pointInTimeRecovery !== undefined
        ? props.pointInTimeRecovery
        : !props.isLocalStack;

    // Billing mode: always PAY_PER_REQUEST (serverless pattern, AAP §0.8.2)
    const resolvedBillingMode: dynamodb.BillingMode =
      props.billingMode ?? dynamodb.BillingMode.PAY_PER_REQUEST;

    // Encryption: AWS managed key by default (AAP §0.8.3)
    const resolvedEncryption: dynamodb.TableEncryption =
      props.encryption ?? dynamodb.TableEncryption.AWS_MANAGED;

    // Partition key: defaults to 'PK' STRING for single-table design
    const resolvedPartitionKey = props.partitionKey ?? {
      name: 'PK',
      type: dynamodb.AttributeType.STRING,
    };

    // Sort key: defaults to 'SK' STRING for single-table design
    const resolvedSortKey = props.sortKey ?? {
      name: 'SK',
      type: dynamodb.AttributeType.STRING,
    };

    // Full table name: {serviceName}-{tableName}
    const fullTableName = `${props.serviceName}-${props.tableName}`;

    // -----------------------------------------------------------------------
    // 3. Create the DynamoDB Table
    // -----------------------------------------------------------------------
    this.table = new dynamodb.Table(this, 'Table', {
      tableName: fullTableName,
      partitionKey: resolvedPartitionKey,
      sortKey: resolvedSortKey,
      billingMode: resolvedBillingMode,
      removalPolicy: resolvedRemovalPolicy,
      pointInTimeRecoverySpecification: {
        pointInTimeRecoveryEnabled: resolvedPointInTimeRecoveryEnabled,
      },
      encryption: resolvedEncryption,
      // TTL is configured via a separate property on Table construct
      timeToLiveAttribute: props.timeToLiveAttribute,
      // DynamoDB Streams for event sourcing / change data capture
      stream: props.stream,
    });

    // -----------------------------------------------------------------------
    // 4. Add Global Secondary Indexes
    // -----------------------------------------------------------------------
    if (props.globalSecondaryIndexes && props.globalSecondaryIndexes.length > 0) {
      for (const gsiDef of props.globalSecondaryIndexes) {
        // Validate GSI definition
        if (!gsiDef.indexName || gsiDef.indexName.trim().length === 0) {
          throw new Error(
            `WebVellaDynamoDBTable (${fullTableName}): GSI indexName is required.`
          );
        }
        if (!gsiDef.partitionKey || !gsiDef.partitionKey.name) {
          throw new Error(
            `WebVellaDynamoDBTable (${fullTableName}): GSI '${gsiDef.indexName}' ` +
              'requires a partitionKey with a name.'
          );
        }

        // Validate nonKeyAttributes only present with INCLUDE projection
        if (
          gsiDef.nonKeyAttributes &&
          gsiDef.nonKeyAttributes.length > 0 &&
          gsiDef.projectionType !== dynamodb.ProjectionType.INCLUDE
        ) {
          throw new Error(
            `WebVellaDynamoDBTable (${fullTableName}): GSI '${gsiDef.indexName}' ` +
              'specifies nonKeyAttributes but projectionType is not INCLUDE.'
          );
        }

        // Default projection type to ALL for maximum query flexibility
        const resolvedProjectionType =
          gsiDef.projectionType ?? dynamodb.ProjectionType.ALL;

        this.table.addGlobalSecondaryIndex({
          indexName: gsiDef.indexName,
          partitionKey: gsiDef.partitionKey,
          sortKey: gsiDef.sortKey,
          projectionType: resolvedProjectionType,
          nonKeyAttributes: gsiDef.nonKeyAttributes,
        });
      }
    }

    // -----------------------------------------------------------------------
    // 5. Apply resource tags (AAP §0.8.5 — operational requirements)
    // -----------------------------------------------------------------------
    cdk.Tags.of(this).add('service', props.serviceName);
    cdk.Tags.of(this).add('resource', 'dynamodb-table');
    cdk.Tags.of(this).add('table-name', fullTableName);
    cdk.Tags.of(this).add(
      'environment',
      props.isLocalStack ? 'localstack' : 'production'
    );

    // -----------------------------------------------------------------------
    // 6. Set exposed properties for cross-stack composition
    // -----------------------------------------------------------------------
    this.tableName = this.table.tableName;
    this.tableArn = this.table.tableArn;
  }
}
