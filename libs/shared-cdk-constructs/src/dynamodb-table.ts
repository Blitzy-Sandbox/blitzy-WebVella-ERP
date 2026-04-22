/**
 * @module dynamodb-table
 * @description Reusable AWS CDK L3 construct for provisioning standard DynamoDB tables
 * following the single-table design pattern. This construct replaces the monolith's
 * single PostgreSQL database infrastructure (DbContext.cs, DbConnection.cs, DbRepository.cs,
 * DbEntityRepository.cs, DbRecordRepository.cs, DbRelationRepository.cs) with per-service
 * DynamoDB tables.
 *
 * Architecture Context:
 * - Replaces PostgreSQL ambient context (DbContext.Current singleton) with isolated per-service tables
 * - Replaces dynamic `rec_{entityName}` table creation with single-table design (PK/SK composite keys)
 * - Replaces PostgreSQL DDL (CREATE TABLE/INDEX) with DynamoDB GSIs
 * - Replaces SQL-based queries with DynamoDB Query/Scan operations
 *
 * Single-Table Design Key Patterns (AAP §0.7.3):
 * - Entity metadata: PK=ENTITY#{entityId}, SK=META
 * - Field definitions: PK=ENTITY#{entityId}, SK=FIELD#{fieldId}
 * - Relation definitions: PK=ENTITY#{entityId}, SK=RELATION#{relationId}
 * - Records: PK=ENTITY#{entityName}, SK=RECORD#{recordId}
 *
 * Used by 8 out of 10 services (all except Invoicing and Reporting which use RDS PostgreSQL).
 *
 * @see AAP §0.4.2 — Database-Per-Service pattern
 * @see AAP §0.7.3 — Dynamic Entity/Field System architecture
 * @see AAP §0.7.4 — Database Migration Strategy
 * @see AAP §0.7.6 — LocalStack Dual-Target CDK Strategy
 * @see AAP §0.8.1 — Single entity ownership
 * @see AAP §0.8.2 — DynamoDB read latency P99 < 10ms (PAY_PER_REQUEST)
 * @see AAP §0.8.3 — Encryption at rest for all datastores
 */

import { Construct } from 'constructs';
import { RemovalPolicy, aws_dynamodb as dynamodb } from 'aws-cdk-lib';

/**
 * Definition for a Global Secondary Index (GSI) on a DynamoDB table.
 *
 * GSIs replace the monolith's PostgreSQL standard/unique indexes, GIST indexes,
 * and GIN FTS indexes (DbRepository.cs lines 60+) with DynamoDB-native secondary
 * index access patterns.
 *
 * Typical GSI patterns consumed by service stacks:
 * - Entity Management: GSI1 (entity name lookups), GSI2 (relation lookups by entity)
 * - CRM: GSI1 (email/name lookups), GSI2 (account→contact relationship queries)
 * - Identity: GSI1 (email lookups and role queries)
 * - Inventory: GSI1 (project-based task lookups), GSI2 (status-based queries)
 * - Notifications: GSI1 (status-based lookups for queued emails)
 * - File Management: GSI1 (file owner/type lookups)
 * - Workflow: GSI1 (status-based lookups), GSI2 (schedule-based lookups)
 * - Plugin System: GSI1 (plugin name/type lookups)
 */
export interface GsiDefinition {
  /**
   * GSI name identifier (e.g., 'GSI1', 'GSI2').
   * Should follow a consistent naming convention across all service tables.
   */
  readonly indexName: string;

  /**
   * GSI partition key attribute name (e.g., 'GSI1PK').
   * In single-table design, this is typically a composite string attribute
   * that enables alternate access patterns beyond the base table's PK.
   */
  readonly partitionKeyName: string;

  /**
   * GSI partition key attribute type.
   * For single-table design, this should always be STRING to support
   * composite key patterns like 'TYPE#value'.
   * Defaults to STRING if not specified during GSI provisioning.
   */
  readonly partitionKeyType: dynamodb.AttributeType;

  /**
   * Optional GSI sort key attribute name (e.g., 'GSI1SK').
   * When provided, enables range queries within the GSI partition.
   * Omit for GSIs that only need hash-based lookups.
   */
  readonly sortKeyName?: string;

  /**
   * Optional GSI sort key attribute type.
   * Defaults to STRING if not specified during GSI provisioning.
   * Only used when sortKeyName is provided.
   */
  readonly sortKeyType?: dynamodb.AttributeType;

  /**
   * Projection type determining which attributes are copied to the GSI.
   * Defaults to ALL for single-table design (all attributes projected)
   * to avoid the need for base table lookups after GSI queries.
   */
  readonly projectionType?: dynamodb.ProjectionType;
}

/**
 * Properties for constructing a DynamoDbTableConstruct.
 *
 * Provides configuration for creating a DynamoDB table following the single-table
 * design pattern, replacing the monolith's PostgreSQL database infrastructure.
 *
 * Key defaults implement the single-table design convention:
 * - Partition key defaults to 'PK' (STRING)
 * - Sort key defaults to 'SK' (STRING)
 * - Billing mode is always PAY_PER_REQUEST (on-demand)
 * - Encryption at rest is always enabled (AWS managed key)
 *
 * @example Identity service table:
 * ```typescript
 * new DynamoDbTableConstruct(this, 'IdentityTable', {
 *   tableName: 'webvella-erp-identity',
 *   isLocalStack: isLocalStack,
 *   gsis: [{
 *     indexName: 'GSI1',
 *     partitionKeyName: 'GSI1PK',
 *     partitionKeyType: dynamodb.AttributeType.STRING,
 *     sortKeyName: 'GSI1SK',
 *     sortKeyType: dynamodb.AttributeType.STRING,
 *   }],
 * });
 * ```
 *
 * @example Entity Management table with streams:
 * ```typescript
 * new DynamoDbTableConstruct(this, 'EntityMetadataTable', {
 *   tableName: 'webvella-erp-entity-metadata',
 *   isLocalStack: isLocalStack,
 *   enableStream: true,
 *   gsis: [
 *     { indexName: 'GSI1', partitionKeyName: 'GSI1PK', partitionKeyType: dynamodb.AttributeType.STRING, sortKeyName: 'GSI1SK' },
 *     { indexName: 'GSI2', partitionKeyName: 'GSI2PK', partitionKeyType: dynamodb.AttributeType.STRING, sortKeyName: 'GSI2SK' },
 *   ],
 * });
 * ```
 */
export interface DynamoDbTableProps {
  /**
   * DynamoDB table name.
   * Should follow the naming convention 'webvella-erp-{service-name}'
   * (e.g., 'webvella-erp-identity', 'webvella-erp-crm', 'webvella-erp-entity-metadata').
   * Each table is exclusively owned by one service per AAP §0.8.1.
   */
  readonly tableName: string;

  /**
   * Partition key attribute name.
   * Defaults to 'PK' following single-table design convention.
   * In single-table design, the PK stores type-prefixed composite keys
   * like 'ENTITY#{entityId}' or 'USER#{userId}'.
   */
  readonly partitionKeyName?: string;

  /**
   * Sort key attribute name.
   * Defaults to 'SK' following single-table design convention.
   * In single-table design, the SK enables multiple item types per partition
   * like 'META', 'FIELD#{fieldId}', 'RECORD#{recordId}'.
   */
  readonly sortKeyName?: string;

  /**
   * Dual-target deployment flag (per AAP §0.7.6).
   * When true (LocalStack mode):
   * - Removal policy is set to DESTROY for clean teardown
   * - Point-in-time recovery is disabled (not needed locally)
   * When false (production mode):
   * - Removal policy is set to RETAIN to prevent data loss
   * - Point-in-time recovery is enabled for disaster recovery
   */
  readonly isLocalStack: boolean;

  /**
   * Array of Global Secondary Index definitions.
   * Most service tables have 1-2 GSIs for alternate access patterns.
   * Each GSI uses on-demand capacity (inherited from table billing mode).
   */
  readonly gsis?: GsiDefinition[];

  /**
   * Enable DynamoDB Streams for change data capture and event sourcing.
   * Defaults to false. When enabled, the stream view type defaults to
   * NEW_AND_OLD_IMAGES for complete change tracking.
   *
   * Used by entity-management service for triggering domain events
   * when entity metadata or records change.
   */
  readonly enableStream?: boolean;

  /**
   * DynamoDB Streams view type specifying which data is written to the stream.
   * Only applicable when enableStream is true.
   * Defaults to NEW_AND_OLD_IMAGES when streams are enabled,
   * providing both the old and new item images for comprehensive change tracking.
   */
  readonly streamViewType?: dynamodb.StreamViewType;

  /**
   * Enable Point-in-Time Recovery (PITR) for continuous backups.
   * When not explicitly set, defaults to:
   * - true when NOT isLocalStack (production disaster recovery)
   * - false when isLocalStack (not needed for local development)
   * Can be overridden explicitly for either environment.
   */
  readonly enablePointInTimeRecovery?: boolean;

  /**
   * Optional TTL attribute name for automatic item expiration.
   * When specified, DynamoDB automatically deletes items after the
   * epoch timestamp stored in this attribute.
   *
   * Common use cases:
   * - 'ttl' for idempotency records (auto-cleanup after processing window)
   * - 'expiresAt' for temporary session or token data
   */
  readonly timeToLiveAttribute?: string;
}

/**
 * L3 CDK construct that provisions a standardized DynamoDB table following
 * the single-table design pattern for the WebVella ERP microservices architecture.
 *
 * This construct replaces the monolith's single PostgreSQL database infrastructure:
 * - DbContext.cs — Ambient context with AsyncLocal tracking → Per-service isolated tables
 * - DbConnection.cs — NpgsqlConnection/Transaction management → AWS SDK DynamoDB client
 * - DbRepository.cs — PostgreSQL DDL (CREATE TABLE/INDEX) → DynamoDB table + GSIs
 * - DbEntityRepository.cs — Entity JSON doc store + rec_* tables → Single-table with PK/SK
 * - DbRecordRepository.cs — SQL query translation → DynamoDB Query/Scan
 * - DbRelationRepository.cs — Relation docs + FK/join tables → Single-table items + GSIs
 *
 * Features:
 * - Single-table design with configurable PK/SK attributes (default: 'PK'/'SK')
 * - On-demand billing (PAY_PER_REQUEST) for automatic scaling
 * - Configurable GSIs for alternate access patterns
 * - Optional DynamoDB Streams for event sourcing
 * - Encryption at rest (AWS managed key)
 * - Dual-target support: LocalStack (DESTROY) vs production (RETAIN + PITR)
 * - Optional TTL for automatic item expiration
 *
 * Consumed by 8 DynamoDB-backed service stacks:
 * - identity-stack: 1 table with GSI1
 * - entity-management-stack: 2 tables (metadata + records), each with GSI1+GSI2, streams enabled
 * - crm-stack: 1 table with GSI1+GSI2
 * - inventory-stack: 1 table with GSI1+GSI2
 * - notifications-stack: 1 table with GSI1
 * - file-management-stack: 1 table with GSI1
 * - workflow-stack: 1 table with GSI1+GSI2
 * - plugin-system-stack: 1 table with GSI1
 */
export class DynamoDbTableConstruct extends Construct {
  /**
   * The underlying DynamoDB Table resource.
   * Provides direct access to the CDK Table construct for advanced configuration
   * such as adding event source mappings, granting permissions, or configuring alarms.
   */
  public readonly table: dynamodb.Table;

  /**
   * The Amazon Resource Name (ARN) of the DynamoDB table.
   * Convenience property used by service stacks to:
   * - Pass to LambdaServiceConstruct for IAM permissions (dynamodb:GetItem, PutItem, Query, etc.)
   * - Configure CloudWatch alarms on table metrics
   * - Set up cross-stack references
   */
  public readonly tableArn: string;

  /**
   * The name of the DynamoDB table.
   * Convenience property used by service stacks to:
   * - Store in SSM Parameter Store for runtime service discovery
   * - Pass as environment variable to Lambda functions
   * - Reference in structured logging for table identification
   */
  public readonly tableName: string;

  /**
   * Creates a new DynamoDB table construct with single-table design defaults.
   *
   * @param scope - The CDK scope (typically a Stack) in which this construct is defined
   * @param id - The construct's unique identifier within the scope
   * @param props - Configuration properties for the DynamoDB table
   *
   * @throws Error if tableName is empty or undefined
   * @throws Error if GSI definitions contain invalid configurations
   */
  constructor(scope: Construct, id: string, props: DynamoDbTableProps) {
    super(scope, id);

    // Validate required properties
    if (!props.tableName || props.tableName.trim().length === 0) {
      throw new Error(
        `DynamoDbTableConstruct: tableName is required and cannot be empty. ` +
        `Received: '${props.tableName}'`
      );
    }

    // Validate GSI definitions if provided
    if (props.gsis) {
      this.validateGsiDefinitions(props.gsis);
    }

    // Determine Point-in-Time Recovery setting:
    // - Explicit prop takes precedence
    // - Otherwise: enabled in production, disabled in LocalStack
    const pointInTimeRecovery = props.enablePointInTimeRecovery !== undefined
      ? props.enablePointInTimeRecovery
      : !props.isLocalStack;

    // Determine stream configuration:
    // When streams are enabled, default to NEW_AND_OLD_IMAGES for complete change tracking
    // This supports event sourcing patterns used by entity-management service
    const streamSpecification = props.enableStream
      ? (props.streamViewType ?? dynamodb.StreamViewType.NEW_AND_OLD_IMAGES)
      : undefined;

    // Create the DynamoDB table with single-table design configuration
    this.table = new dynamodb.Table(this, 'Table', {
      // Table identity
      tableName: props.tableName,

      // Single-table design key schema:
      // PK stores type-prefixed keys (e.g., 'ENTITY#{entityId}', 'USER#{userId}')
      // SK stores item-type qualifiers (e.g., 'META', 'FIELD#{fieldId}', 'RECORD#{recordId}')
      partitionKey: {
        name: props.partitionKeyName ?? 'PK',
        type: dynamodb.AttributeType.STRING,
      },
      sortKey: {
        name: props.sortKeyName ?? 'SK',
        type: dynamodb.AttributeType.STRING,
      },

      // On-demand billing for automatic scaling without capacity planning
      // Meets AAP §0.8.2: DynamoDB read latency P99 < 10ms
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,

      // Dual-target removal policy (AAP §0.7.6):
      // LocalStack: DESTROY for clean teardown during development
      // Production: RETAIN to prevent accidental data loss (AAP §0.8.1)
      removalPolicy: props.isLocalStack
        ? RemovalPolicy.DESTROY
        : RemovalPolicy.RETAIN,

      // Point-in-Time Recovery for continuous backups:
      // Enabled in production for disaster recovery
      // Disabled in LocalStack (not needed locally)
      // Uses pointInTimeRecoverySpecification (non-deprecated API in CDK v2.239+)
      pointInTimeRecoverySpecification: {
        pointInTimeRecoveryEnabled: pointInTimeRecovery,
      },

      // Encryption at rest with AWS managed key (AAP §0.8.3):
      // All datastores must have encryption at rest enabled
      encryption: dynamodb.TableEncryption.AWS_MANAGED,

      // DynamoDB Streams for change data capture (optional):
      // Used by entity-management service for triggering domain events
      stream: streamSpecification,

      // Time-to-Live for automatic item expiration (optional):
      // Used for idempotency records, session data, temporary tokens
      timeToLiveAttribute: props.timeToLiveAttribute,
    });

    // Provision Global Secondary Indexes for alternate access patterns
    // GSIs replace PostgreSQL indexes (standard, GIST, GIN FTS) from DbRepository.cs
    if (props.gsis && props.gsis.length > 0) {
      this.provisionGlobalSecondaryIndexes(props.gsis);
    }

    // Expose convenience properties for consumption by service stacks
    this.tableArn = this.table.tableArn;
    this.tableName = this.table.tableName;
  }

  /**
   * Validates all GSI definitions for correctness and single-table design compatibility.
   *
   * Ensures:
   * - Each GSI has a non-empty indexName
   * - Each GSI has a non-empty partitionKeyName
   * - Sort key type is only specified when sort key name is provided
   * - Index names are unique within the table
   *
   * @param gsis - Array of GSI definitions to validate
   * @throws Error if any GSI definition is invalid
   */
  private validateGsiDefinitions(gsis: GsiDefinition[]): void {
    const indexNames = new Set<string>();

    for (const gsi of gsis) {
      // Validate indexName is non-empty
      if (!gsi.indexName || gsi.indexName.trim().length === 0) {
        throw new Error(
          `DynamoDbTableConstruct: GSI indexName is required and cannot be empty.`
        );
      }

      // Validate unique index names
      if (indexNames.has(gsi.indexName)) {
        throw new Error(
          `DynamoDbTableConstruct: Duplicate GSI indexName '${gsi.indexName}'. ` +
          `Each GSI must have a unique index name.`
        );
      }
      indexNames.add(gsi.indexName);

      // Validate partitionKeyName is non-empty
      if (!gsi.partitionKeyName || gsi.partitionKeyName.trim().length === 0) {
        throw new Error(
          `DynamoDbTableConstruct: GSI '${gsi.indexName}' partitionKeyName is required ` +
          `and cannot be empty.`
        );
      }

      // Validate sort key type is not specified without sort key name
      if (gsi.sortKeyType && !gsi.sortKeyName) {
        throw new Error(
          `DynamoDbTableConstruct: GSI '${gsi.indexName}' has sortKeyType specified ` +
          `but no sortKeyName. Provide sortKeyName when specifying sortKeyType.`
        );
      }
    }
  }

  /**
   * Provisions Global Secondary Indexes on the DynamoDB table.
   *
   * Each GSI provides an alternate access pattern beyond the base table's PK/SK.
   * In single-table design, GSIs enable queries like:
   * - Entity name lookups (entity-management)
   * - Email/name searches (CRM, identity)
   * - Status-based queries (notifications, workflow)
   * - Relationship traversals (CRM account→contact)
   *
   * All GSIs use on-demand capacity inherited from the table's PAY_PER_REQUEST billing mode.
   * Default projection type is ALL to avoid base table lookups after GSI queries.
   *
   * @param gsis - Array of validated GSI definitions to provision
   */
  private provisionGlobalSecondaryIndexes(gsis: GsiDefinition[]): void {
    for (const gsi of gsis) {
      // Build sort key configuration if specified
      // Sort keys enable range queries within the GSI partition
      const sortKeyConfig = gsi.sortKeyName
        ? {
            name: gsi.sortKeyName,
            type: gsi.sortKeyType ?? dynamodb.AttributeType.STRING,
          }
        : undefined;

      // Add the GSI with all configuration provided at construction time
      // (CDK props interfaces use readonly properties, so we construct the full object at once)
      this.table.addGlobalSecondaryIndex({
        indexName: gsi.indexName,
        partitionKey: {
          name: gsi.partitionKeyName,
          type: gsi.partitionKeyType ?? dynamodb.AttributeType.STRING,
        },
        // Optional sort key for range queries
        sortKey: sortKeyConfig,
        // Default projection to ALL for single-table design:
        // Projects all attributes to avoid the need for base table lookups
        projectionType: gsi.projectionType ?? dynamodb.ProjectionType.ALL,
      });
    }
  }
}
