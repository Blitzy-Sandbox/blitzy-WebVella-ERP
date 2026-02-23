/**
 * EntityManagementStack — Core Entity Engine Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the Entity Management bounded
 * context — the LARGEST and most critical service in the WebVella ERP
 * microservices architecture. It replaces the monolith's tightly coupled
 * entity/field/relation/record management subsystem with a DynamoDB-backed
 * serverless service.
 *
 * **Source systems replaced:**
 * - `EntityManager.cs` — Entity/field metadata CRUD with validation, manages
 *   `entities` JSON doc store and dynamically creates `rec_*` PostgreSQL
 *   tables. 20+ field types (AutoNumber, Checkbox, Currency, Date, DateTime,
 *   Email, File, Guid, Html, Image, MultiLineText, MultiSelect, Number,
 *   Password, Percent, Phone, Select, Text, Url, Geography, TreeSelect).
 * - `EntityRelationManager.cs` — Relation CRUD with immutability rules and
 *   validation for origin/target entity fields. Manages `entity_relations`
 *   JSON doc store and FK/join tables.
 * - `RecordManager.cs` — Record CRUD with pre/post hook execution, field
 *   type processing, relation handling, security permission checks. The most
 *   complex service file in the monolith with relation separator navigation
 *   (`$`/`$$` notation) and many-to-many relation management.
 * - `DataSourceManager.cs` — Code + DB datasource registry and execution
 *   engine with IMemoryCache caching. Manages `data_source` table.
 * - `SearchManager.cs` — PostgreSQL FTS search using ILIKE contains and
 *   ts_query full-text search against `system_search` table.
 * - `ImportExportManager.cs` — CSV import/export pipelines using CsvHelper
 *   for bulk record operations with relation separator handling.
 * - `DbEntityRepository.cs` — Entity JSON document persistence with
 *   `RECORD_COLLECTION_PREFIX = "rec_"` table DDL management.
 * - `DbRecordRepository.cs` — Dynamic record queries with SQL translation
 *   using `row_to_json()`, OTM/MTM relation templates, and field separators.
 * - `EqlBuilder.cs` — Irony-based EQL grammar parser (SELECT/FROM/WHERE/
 *   ORDER/PAGE) translated to PostgreSQL SQL. Replaced by DynamoDB query
 *   adapter per AAP §0.7.1 EQL decomposition strategy.
 * - `Cache.cs` — IMemoryCache wrapper for entity/relation metadata with
 *   1-hour expiration. Replaced by DynamoDB consistent reads.
 *
 * **Target architecture:**
 * - 2 DynamoDB tables (single-table design each):
 *   - Metadata table: entity/field/relation/datasource definitions
 *   - Records table: entity record data
 * - 7 Lambda functions (.NET 9 Native AOT) for domain-specific operations
 * - SNS domain events for cross-service communication
 * - SSM Parameter Store for resource discovery
 * - DynamoDB Streams for event sourcing (change data capture)
 *
 * Resources created:
 *
 * 1. **DynamoDB Metadata Table** (`entity-management-metadata`) —
 *    Single-table design storing all entity, field, relation, and datasource
 *    definitions. Key patterns per AAP §0.7.3:
 *    - `PK=ENTITY#{entityId}, SK=META` — Entity definition (name, label,
 *      icon, system flag, permissions, weight, record permissions)
 *    - `PK=ENTITY#{entityId}, SK=FIELD#{fieldId}` — Field definition (name,
 *      label, type from 20+ field types, required, unique, searchable,
 *      default value, options, permissions)
 *    - `PK=ENTITY#{entityId}, SK=RELATION#{relationId}` — Relation reference
 *      (origin entity, target entity, relation type: 1:1, 1:N, N:N)
 *    - `PK=RELATION#{relationId}, SK=META` — Relation definition (name,
 *      label, origin entity, target entity, relation type, system flag)
 *    - `PK=DATASOURCE#{dsId}, SK=META` — Datasource definition (name, type,
 *      EQL/code, parameters, weight)
 *    GSI1: `GSI1PK`/`GSI1SK` — Entity name lookups: `GSI1PK=NAME#{name}`
 *    GSI2: `GSI2PK`/`GSI2SK` — Relation lookups by entity:
 *      `GSI2PK=REL_ENTITY#{entityId}`
 *    Stream: NEW_AND_OLD_IMAGES for metadata change events.
 *
 * 2. **DynamoDB Records Table** (`entity-management-records`) —
 *    Single-table design storing all entity record data. Key patterns:
 *    - `PK=ENTITY#{entityName}, SK=RECORD#{recordId}` — Record data (all
 *      field values serialized as DynamoDB attributes per field type mapping)
 *    GSI1: `GSI1PK`/`GSI1SK` — Search-indexed lookups for searchable fields
 *    GSI2: `GSI2PK`/`GSI2SK` — Relation-based queries (e.g., all records
 *      related to a specific record via a defined relation)
 *    Stream: NEW_AND_OLD_IMAGES for record change events.
 *
 * 3. **Lambda Functions** (7 handlers, .NET 9 Native AOT):
 *    - `webvella-entity-management-entity` (512 MB, 60s) — Entity CRUD
 *    - `webvella-entity-management-field` (512 MB, 60s) — Field CRUD
 *    - `webvella-entity-management-relation` (512 MB, 60s) — Relation CRUD
 *    - `webvella-entity-management-record` (512 MB, 60s) — Record CRUD
 *    - `webvella-entity-management-datasource` (512 MB, 60s) — Datasource exec
 *    - `webvella-entity-management-search` (512 MB, 60s) — Search operations
 *    - `webvella-entity-management-import-export` (512 MB, 300s) — CSV bulk ops
 *
 * 4. **SSM Parameters**:
 *    - `/webvella-erp/entity-management/metadata-table-name`
 *    - `/webvella-erp/entity-management/records-table-name`
 *
 * Domain events published to the shared SNS event bus:
 * - `entity-management.entity.created` — New entity definition created
 * - `entity-management.entity.updated` — Entity definition updated
 * - `entity-management.entity.deleted` — Entity definition deleted
 * - `entity-management.field.created` — New field added to entity
 * - `entity-management.field.updated` — Field definition updated
 * - `entity-management.field.deleted` — Field removed from entity
 * - `entity-management.relation.created` — New relation created
 * - `entity-management.relation.updated` — Relation definition updated
 * - `entity-management.relation.deleted` — Relation deleted
 * - `entity-management.record.created` — New record created
 * - `entity-management.record.updated` — Record updated
 * - `entity-management.record.deleted` — Record deleted
 *
 * Source files referenced:
 * - WebVella.Erp/Api/EntityManager.cs — Entity/field metadata CRUD
 * - WebVella.Erp/Api/RecordManager.cs — Record CRUD with hooks
 * - WebVella.Erp/Api/EntityRelationManager.cs — Relation CRUD
 * - WebVella.Erp/Api/DataSourceManager.cs — Datasource registry/execution
 * - WebVella.Erp/Api/SearchManager.cs — FTS search index
 * - WebVella.Erp/Api/ImportExportManager.cs — CSV import/export
 * - WebVella.Erp/Database/DbEntityRepository.cs — Entity JSON doc store
 * - WebVella.Erp/Database/DbRecordRepository.cs — Dynamic record queries
 * - WebVella.Erp/Eql/EqlBuilder.cs — EQL → AST → SQL translation
 * - WebVella.Erp/Api/Cache.cs — Entity/relation metadata caching
 *
 * @module infra/src/stacks/entity-management-stack
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
// Interface: EntityManagementStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the EntityManagementStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface EntityManagementStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - Removal policies: DESTROY (LocalStack) vs RETAIN (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   * - DynamoDB point-in-time recovery (disabled for LocalStack)
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. All 7 Lambda functions publish domain events
   * to this topic using the naming convention from AAP §0.8.5:
   * - `entity-management.entity.created/updated/deleted`
   * - `entity-management.field.created/updated/deleted`
   * - `entity-management.relation.created/updated/deleted`
   * - `entity-management.record.created/updated/deleted`
   *
   * Replaces the monolith's synchronous HookManager and RecordHookManager
   * post-hook invocations (IErpPostCreateRecordHook, IErpPostUpdateRecordHook,
   * IErpPostDeleteRecordHook) with asynchronous SNS event publishing per
   * AAP §0.7.2 hook-to-event migration strategy.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: EntityManagementStack
// ---------------------------------------------------------------------------

/**
 * EntityManagementStack — CDK stack for the Core Entity Engine bounded context.
 *
 * This is the LARGEST and most critical stack in the WebVella ERP
 * microservices architecture. It owns all entity definitions, field
 * definitions, relation definitions, datasource definitions, and their
 * associated record data.
 *
 * The stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * tables, Lambda functions, IAM policies, and SSM parameters. No other
 * service may directly access the Entity Management service's datastore.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack
 * and other dependent stacks:
 * - `functions` — Array of 7 Lambda function references for API Gateway routes
 * - `metadataTableName` — Metadata DynamoDB table name
 * - `recordsTableName` — Records DynamoDB table name
 */
export class EntityManagementStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains all 7 handler functions that serve the Entity Management
   * service's HTTP endpoints. Consumed by ApiGatewayStack for path-based
   * routing under `/v1/entity-management/*`.
   *
   * Index 0: EntityHandler — entity CRUD operations
   * Index 1: FieldHandler — field CRUD within entities
   * Index 2: RelationHandler — relation CRUD operations
   * Index 3: RecordHandler — record CRUD with domain event publishing
   * Index 4: DataSourceHandler — datasource execution
   * Index 5: SearchHandler — search operations via DynamoDB GSI
   * Index 6: ImportExportHandler — CSV import/export (300s timeout)
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB metadata table name for entity/field/relation definitions.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/entity-management/metadata-table-name`.
   */
  public readonly metadataTableName: string;

  /**
   * DynamoDB records table name for entity record data.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/entity-management/records-table-name`.
   */
  public readonly recordsTableName: string;

  constructor(scope: Construct, id: string, props: EntityManagementStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Tables — Single-table design for Entity Management
    // -----------------------------------------------------------------------

    // -----------------------------------------------------------------------
    // 1a. Metadata Table — Entity/field/relation/datasource definitions
    // -----------------------------------------------------------------------
    // Replaces the monolith's `entities` and `entity_relations` JSON document
    // tables, plus the `data_source` table from DbDataSourceRepository.
    //
    // Single-table design access patterns per AAP §0.7.3:
    //
    //   PK=ENTITY#{entityId},       SK=META                → Entity definition
    //     Fields: name, label, label_plural, icon, system, weight, color,
    //     record_permissions, record_screen_id_field.
    //     (Source: EntityManager.cs entity CRUD, DbEntityRepository.Create())
    //
    //   PK=ENTITY#{entityId},       SK=FIELD#{fieldId}     → Field definition
    //     Fields: name, label, type (20+ types: AutoNumber, Checkbox,
    //     Currency, Date, DateTime, Email, File, Guid, Html, Image,
    //     MultiLineText, MultiSelect, Number, Password, Percent, Phone,
    //     Select, Text, Url, Geography, TreeSelect), required, unique,
    //     searchable, system, default_value, options, permissions.
    //     (Source: EntityManager.cs field CRUD, FieldTypes/*.cs)
    //
    //   PK=ENTITY#{entityId},       SK=RELATION#{relId}    → Relation ref
    //     Denormalized relation reference for entity-scoped relation lookups.
    //     Points to the canonical relation definition below.
    //
    //   PK=RELATION#{relationId},   SK=META                → Relation definition
    //     Fields: name, label, description, system, relation_type (1:1, 1:N,
    //     N:N), origin_entity_id, origin_field_id, target_entity_id,
    //     target_field_id.
    //     (Source: EntityRelationManager.cs, DbRelationRepository)
    //
    //   PK=DATASOURCE#{dsId},       SK=META                → Datasource def
    //     Fields: name, description, type (eql/code), eql_text, parameters,
    //     weight, result_model.
    //     (Source: DataSourceManager.cs, DbDataSourceRepository)
    //
    // GSI1 — Entity name lookups (for resolving entity by name instead of ID):
    //   GSI1PK=NAME#{entityName}, GSI1SK=ENTITY#{entityId}
    //   GSI1PK=DSNAME#{dsName},  GSI1SK=DATASOURCE#{dsId}
    //   Enables the EntityManager.ReadEntity(name) and DataSourceManager.Get(name)
    //   access patterns that were previously direct SQL queries.
    //
    // GSI2 — Relation lookups by entity (find all relations for an entity):
    //   GSI2PK=REL_ORIGIN#{originEntityId}, GSI2SK=RELATION#{relationId}
    //   GSI2PK=REL_TARGET#{targetEntityId}, GSI2SK=RELATION#{relationId}
    //   Replaces the monolith's entity_relations table scan filtered by
    //   origin_entity_id or target_entity_id from EntityRelationManager.

    const metadataGsiDefinitions: GsiDefinition[] = [
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

    const metadataTable = new WebVellaDynamoDBTable(this, 'MetadataTable', {
      serviceName: 'entity-management',
      tableName: 'metadata',
      isLocalStack,
      globalSecondaryIndexes: metadataGsiDefinitions,
      stream: dynamodb.StreamViewType.NEW_AND_OLD_IMAGES,
    });

    // -----------------------------------------------------------------------
    // 1b. Records Table — Entity record data storage
    // -----------------------------------------------------------------------
    // Replaces all monolith `rec_{entityName}` dynamic PostgreSQL tables that
    // were created by DbEntityRepository.Create() and queried by
    // DbRecordRepository with SQL translation from EqlBuilder.Sql.cs.
    //
    // Single-table design access patterns per AAP §0.7.3:
    //
    //   PK=ENTITY#{entityName},     SK=RECORD#{recordId}   → Record data
    //     All field values serialized as DynamoDB attributes according to
    //     the field type → DynamoDB attribute type mapping:
    //       AutoNumber → NUMBER
    //       Checkbox → BOOLEAN
    //       Currency/Number/Percent → NUMBER
    //       Date/DateTime → STRING (ISO 8601)
    //       Email/Phone/Text/Url/Html/MultiLineText → STRING
    //       File/Image → STRING (S3 key reference)
    //       Guid → STRING
    //       MultiSelect/TreeSelect → LIST of STRING
    //       Select → STRING
    //       Password → STRING (hashed)
    //       Geography → MAP {latitude, longitude}
    //     (Source: RecordManager.cs CRUD, DbRecordRepository, FieldTypes/*.cs)
    //
    // GSI1 — Search-indexed lookups for searchable fields:
    //   GSI1PK=SEARCH#{entityName}, GSI1SK=INDEX#{searchableValue}
    //   Replaces PostgreSQL FTS from SearchManager.cs and system_search table
    //   with DynamoDB GSI-based search patterns.
    //
    // GSI2 — Relation-based queries (records related via defined relations):
    //   GSI2PK=REL#{relationId}#{originRecordId},
    //   GSI2SK=RECORD#{targetRecordId}
    //   Enables RecordManager.Find() with relation navigation ($relation
    //   and $$relation notation from DbRecordRepository OTM/MTM templates).

    const recordsGsiDefinitions: GsiDefinition[] = [
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

    const recordsTable = new WebVellaDynamoDBTable(this, 'RecordsTable', {
      serviceName: 'entity-management',
      tableName: 'records',
      isLocalStack,
      globalSecondaryIndexes: recordsGsiDefinitions,
      stream: dynamodb.StreamViewType.NEW_AND_OLD_IMAGES,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the metadata table and its GSIs.
    // All 7 Lambda functions need at least read access to entity/field/relation
    // metadata. EntityHandler, FieldHandler, RelationHandler, and
    // DataSourceHandler need write access for metadata mutations.
    const metadataTablePolicy = new iam.PolicyStatement({
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
        metadataTable.tableArn,
        `${metadataTable.tableArn}/index/*`,
      ],
    });

    // DynamoDB CRUD permissions scoped to the records table and its GSIs.
    // RecordHandler, SearchHandler, ImportExportHandler, and DataSourceHandler
    // need full CRUD access for record operations. EntityHandler needs write
    // access for initial default record seeding on entity creation.
    const recordsTablePolicy = new iam.PolicyStatement({
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
        recordsTable.tableArn,
        `${recordsTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // All 7 Lambda functions publish domain events following the naming
    // convention from AAP §0.8.5: `entity-management.{entity}.{action}`.
    // This replaces the monolith's synchronous HookManager and
    // RecordHookManager post-hook invocations (IErpPostCreateRecordHook,
    // IErpPostUpdateRecordHook, IErpPostDeleteRecordHook) with asynchronous
    // SNS event publishing per AAP §0.7.2.
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sns:Publish',
      ],
      resources: [
        eventBus.topicArn,
      ],
    });

    // Combined policies array shared by all Lambda functions.
    // Each function gets access to both DynamoDB tables (metadata + records)
    // and the SNS event bus for domain event publishing.
    const allPolicies: iam.PolicyStatement[] = [
      metadataTablePolicy,
      recordsTablePolicy,
      snsPublishPolicy,
    ];

    // Common environment variables for all Lambda functions.
    // These replace the monolith's DbContext.Current ambient singleton
    // and HookManager configuration with explicit Lambda environment bindings.
    const commonEnvironment: { [key: string]: string } = {
      METADATA_TABLE_NAME: metadataTable.tableName,
      RECORDS_TABLE_NAME: recordsTable.tableName,
      EVENT_TOPIC_ARN: eventBus.topicArn,
    };

    // -----------------------------------------------------------------------
    // 3. Lambda Functions — .NET 9 Native AOT handlers
    // -----------------------------------------------------------------------

    // 3a. EntityHandler — Entity CRUD operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/entities             → Create entity
    //   GET    /v1/entity-management/entities             → List entities
    //   GET    /v1/entity-management/entities/{entityId}  → Get entity details
    //   PUT    /v1/entity-management/entities/{entityId}  → Update entity
    //   DELETE /v1/entity-management/entities/{entityId}  → Delete entity
    //
    // Source mapping:
    //   EntityManager.cs → CreateEntity(), ReadEntity(), ReadEntities(),
    //     UpdateEntity(), DeleteEntity() with validation (ValidateEntity),
    //     field auto-creation (id field), and entity name uniqueness checks.
    //   DbEntityRepository.cs → Create(), Read(), Update(), Delete() for
    //     entity JSON document persistence and rec_* table DDL management.
    //   Cache.cs → Entity metadata caching replaced by DynamoDB consistent reads.
    //
    // Publishes domain events:
    //   entity-management.entity.created — after successful entity creation
    //   entity-management.entity.updated — after successful entity update
    //   entity-management.entity.deleted — after successful entity deletion

    const entityHandler = new WebVellaLambdaService(this, 'EntityHandler', {
      serviceName: 'entity-management',
      functionName: 'entity',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/entity-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Entity Management EntityHandler — entity definition CRUD replacing ' +
        'EntityManager.cs and DbEntityRepository.cs with DynamoDB persistence.',
      environment: commonEnvironment,
      additionalPolicies: allPolicies,
    });

    // 3b. FieldHandler — Field CRUD within entities
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/entities/{entityId}/fields            → Create field
    //   GET    /v1/entity-management/entities/{entityId}/fields            → List fields
    //   GET    /v1/entity-management/entities/{entityId}/fields/{fieldId}  → Get field
    //   PUT    /v1/entity-management/entities/{entityId}/fields/{fieldId}  → Update field
    //   DELETE /v1/entity-management/entities/{entityId}/fields/{fieldId}  → Delete field
    //
    // Source mapping:
    //   EntityManager.cs → CreateField(), ReadField(), UpdateField(),
    //     DeleteField() with field type validation, default value processing,
    //     and system field protection. Handles 20+ field types from
    //     WebVella.Erp/Database/FieldTypes/*.cs.
    //   DbEntityRepository.cs → Field management within entity JSON docs
    //     and corresponding rec_* table column DDL operations.
    //
    // Publishes domain events:
    //   entity-management.field.created — after successful field creation
    //   entity-management.field.updated — after successful field update
    //   entity-management.field.deleted — after successful field deletion

    const fieldHandler = new WebVellaLambdaService(this, 'FieldHandler', {
      serviceName: 'entity-management',
      functionName: 'field',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/entity-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Entity Management FieldHandler — field definition CRUD within ' +
        'entities, supporting 20+ field types from EntityManager.cs.',
      environment: commonEnvironment,
      additionalPolicies: allPolicies,
    });

    // 3c. RelationHandler — Relation CRUD operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/relations             → Create relation
    //   GET    /v1/entity-management/relations             → List relations
    //   GET    /v1/entity-management/relations/{relId}     → Get relation
    //   PUT    /v1/entity-management/relations/{relId}     → Update relation
    //   DELETE /v1/entity-management/relations/{relId}     → Delete relation
    //
    // Source mapping:
    //   EntityRelationManager.cs → Create(), Read(), Update(), Delete()
    //     with immutability rules (name length <= 63 chars, system relation
    //     protection, origin/target entity validation, relation type
    //     enforcement for 1:1, 1:N, N:N).
    //   DbRelationRepository → Relation JSON doc persistence and FK/join
    //     table management. N:N relations create junction tables.
    //
    // Publishes domain events:
    //   entity-management.relation.created — after successful relation creation
    //   entity-management.relation.updated — after successful relation update
    //   entity-management.relation.deleted — after successful relation deletion

    const relationHandler = new WebVellaLambdaService(this, 'RelationHandler', {
      serviceName: 'entity-management',
      functionName: 'relation',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/entity-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Entity Management RelationHandler — relation definition CRUD ' +
        'replacing EntityRelationManager.cs with DynamoDB persistence.',
      environment: commonEnvironment,
      additionalPolicies: allPolicies,
    });

    // 3d. RecordHandler — Record CRUD with domain event publishing
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/records/{entityName}             → Create record
    //   GET    /v1/entity-management/records/{entityName}             → List records
    //   GET    /v1/entity-management/records/{entityName}/{recordId}  → Get record
    //   PUT    /v1/entity-management/records/{entityName}/{recordId}  → Update record
    //   DELETE /v1/entity-management/records/{entityName}/{recordId}  → Delete record
    //   POST   /v1/entity-management/records/{entityName}/query       → EQL-like query
    //   POST   /v1/entity-management/records/relations/m2m            → M2M relation CRUD
    //
    // Source mapping:
    //   RecordManager.cs → CreateRecord(), Find(), UpdateRecord(),
    //     DeleteRecord(), CreateRelationManyToManyRecord(),
    //     RemoveRelationManyToManyRecord() with field type processing
    //     (auto-number generation, password hashing, date normalization,
    //     file reference validation, geography coordinate parsing,
    //     multiselect array handling), relation navigation ($/$$ notation),
    //     and security permission checks.
    //   DbRecordRepository.cs → Dynamic record CRUD with SQL translation
    //     from EqlBuilder.Sql.cs, row_to_json results, OTM/MTM relation
    //     templates, and field separator handling.
    //   EqlBuilder.cs → EQL query parser replaced by DynamoDB query adapter
    //     per AAP §0.7.1 EQL decomposition strategy.
    //
    // Publishes domain events:
    //   entity-management.record.created — after successful record creation
    //   entity-management.record.updated — after successful record update
    //   entity-management.record.deleted — after successful record deletion

    const recordHandler = new WebVellaLambdaService(this, 'RecordHandler', {
      serviceName: 'entity-management',
      functionName: 'record',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/entity-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Entity Management RecordHandler — record CRUD with domain event ' +
        'publishing, replacing RecordManager.cs and DbRecordRepository.cs.',
      environment: commonEnvironment,
      additionalPolicies: allPolicies,
    });

    // 3e. DataSourceHandler — Datasource execution
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/datasources             → Create datasource
    //   GET    /v1/entity-management/datasources             → List datasources
    //   GET    /v1/entity-management/datasources/{dsId}      → Get datasource
    //   PUT    /v1/entity-management/datasources/{dsId}      → Update datasource
    //   DELETE /v1/entity-management/datasources/{dsId}      → Delete datasource
    //   POST   /v1/entity-management/datasources/{dsId}/exec → Execute datasource
    //
    // Source mapping:
    //   DataSourceManager.cs → Create(), Get(), GetAll(), Delete(),
    //     Execute() with code + DB datasource registry, IMemoryCache
    //     caching (replaced by DynamoDB reads), and EQL execution
    //     pipeline (replaced by DynamoDB Query operations).
    //   DbDataSourceRepository → data_source table CRUD replaced by
    //     metadata DynamoDB table items with PK=DATASOURCE#{dsId}.
    //
    // Publishes domain events:
    //   entity-management.datasource.created — after datasource creation
    //   entity-management.datasource.updated — after datasource update
    //   entity-management.datasource.deleted — after datasource deletion

    const dataSourceHandler = new WebVellaLambdaService(
      this,
      'DataSourceHandler',
      {
        serviceName: 'entity-management',
        functionName: 'datasource',
        runtime: LambdaRuntime.DOTNET_9_AOT,
        codePath: '../services/entity-management/src',
        handler: 'bootstrap',
        isLocalStack,
        memorySize: 512,
        timeoutSeconds: 60,
        description:
          'Entity Management DataSourceHandler — datasource registry and ' +
          'execution replacing DataSourceManager.cs with DynamoDB backend.',
        environment: commonEnvironment,
        additionalPolicies: allPolicies,
      }
    );

    // 3f. SearchHandler — Search operations via DynamoDB GSI
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/search         → Full-text search
    //   GET    /v1/entity-management/search/index    → Rebuild search index
    //
    // Source mapping:
    //   SearchManager.cs → Search() with PostgreSQL FTS (ILIKE contains,
    //     ts_query full-text) replaced by DynamoDB GSI1 search-indexed
    //     lookups on the records table. The system_search table's url,
    //     snippet, timestamp, content fields are replicated as GSI1
    //     attributes.
    //   Fts/FtsAnalyzer — Bulgarian full-text analysis deferred to
    //     future localization pass per AAP §0.3.2 scope exclusion.
    //
    // No domain events published (search is read-only).

    const searchHandler = new WebVellaLambdaService(this, 'SearchHandler', {
      serviceName: 'entity-management',
      functionName: 'search',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/entity-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Entity Management SearchHandler — search operations using DynamoDB ' +
        'GSI, replacing SearchManager.cs PostgreSQL FTS with GSI lookups.',
      environment: commonEnvironment,
      additionalPolicies: allPolicies,
    });

    // 3g. ImportExportHandler — CSV import/export (extended timeout)
    //
    // Handles HTTP endpoints:
    //   POST   /v1/entity-management/import/{entityName}  → Import CSV records
    //   POST   /v1/entity-management/export/{entityName}  → Export records to CSV
    //
    // Source mapping:
    //   ImportExportManager.cs → ImportEntityRecordsFromCsv(),
    //     ExportEntityRecordsToCsv() with CsvHelper processing, relation
    //     separator handling (RELATION_SEPARATOR '.', RELATION_NAME_RESULT_
    //     SEPARATOR '$'), bulk record creation/update via RecordManager,
    //     and field mapping between CSV columns and entity field names.
    //
    // Uses 300s timeout (5 minutes) for large CSV file processing, compared
    // to the standard 60s timeout for other handlers. This accounts for
    // bulk DynamoDB BatchWriteItem operations with exponential backoff.
    //
    // Publishes domain events:
    //   entity-management.record.created — for each newly imported record
    //   entity-management.record.updated — for each updated imported record

    const importExportHandler = new WebVellaLambdaService(
      this,
      'ImportExportHandler',
      {
        serviceName: 'entity-management',
        functionName: 'import-export',
        runtime: LambdaRuntime.DOTNET_9_AOT,
        codePath: '../services/entity-management/src',
        handler: 'bootstrap',
        isLocalStack,
        memorySize: 512,
        timeoutSeconds: 300,
        description:
          'Entity Management ImportExportHandler — CSV bulk import/export ' +
          'replacing ImportExportManager.cs with 300s timeout for large files.',
        environment: commonEnvironment,
        additionalPolicies: allPolicies,
      }
    );

    // -----------------------------------------------------------------------
    // 4. SSM Parameters — Table names for cross-service discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use these parameters to locate
    // the Entity Management service's datastore without hardcoded names.
    // The seed-test-data.sh and run-migrations.sh scripts read these
    // parameters to configure test data insertion.

    const metadataTableNameParam = new ssm.StringParameter(
      this,
      'MetadataTableNameParam',
      {
        parameterName: '/webvella-erp/entity-management/metadata-table-name',
        stringValue: metadataTable.tableName,
        description:
          'DynamoDB table name for the Entity Management service metadata ' +
          'datastore (entity/field/relation/datasource definitions). ' +
          'Used by bootstrap scripts and cross-service discovery.',
      }
    );

    // Apply conditional removal policy for clean teardown in LocalStack
    // mode per AAP §0.7.6 dual-target strategy.
    metadataTableNameParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    const recordsTableNameParam = new ssm.StringParameter(
      this,
      'RecordsTableNameParam',
      {
        parameterName: '/webvella-erp/entity-management/records-table-name',
        stringValue: recordsTable.tableName,
        description:
          'DynamoDB table name for the Entity Management service records ' +
          'datastore (entity record data). ' +
          'Used by bootstrap scripts and cross-service discovery.',
      }
    );

    // Apply conditional removal policy for records table SSM parameter.
    recordsTableNameParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    // -----------------------------------------------------------------------
    // 5. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [
      entityHandler.function,
      fieldHandler.function,
      relationHandler.function,
      recordHandler.function,
      dataSourceHandler.function,
      searchHandler.function,
      importExportHandler.function,
    ];

    this.metadataTableName = metadataTable.tableName;
    this.recordsTableName = recordsTable.tableName;

    // -----------------------------------------------------------------------
    // 6. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------
    // These outputs are consumed by ApiGatewayStack for route integration,
    // by the Reporting service for event-sourced read model projections,
    // and by the CI/CD pipeline for deployment verification.

    new cdk.CfnOutput(this, 'MetadataTableName', {
      value: metadataTable.tableName,
      description:
        'DynamoDB table name for entity/field/relation/datasource metadata',
      exportName: `${this.stackName}-MetadataTableName`,
    });

    new cdk.CfnOutput(this, 'MetadataTableArn', {
      value: metadataTable.tableArn,
      description: 'DynamoDB table ARN for entity metadata',
      exportName: `${this.stackName}-MetadataTableArn`,
    });

    new cdk.CfnOutput(this, 'RecordsTableName', {
      value: recordsTable.tableName,
      description: 'DynamoDB table name for entity record data',
      exportName: `${this.stackName}-RecordsTableName`,
    });

    new cdk.CfnOutput(this, 'RecordsTableArn', {
      value: recordsTable.tableArn,
      description: 'DynamoDB table ARN for entity record data',
      exportName: `${this.stackName}-RecordsTableArn`,
    });

    new cdk.CfnOutput(this, 'EntityHandlerFunctionArn', {
      value: entityHandler.functionArn,
      description: 'ARN of the EntityHandler Lambda function',
      exportName: `${this.stackName}-EntityHandlerArn`,
    });

    new cdk.CfnOutput(this, 'EntityHandlerFunctionName', {
      value: entityHandler.functionName,
      description: 'Name of the EntityHandler Lambda function',
      exportName: `${this.stackName}-EntityHandlerName`,
    });

    new cdk.CfnOutput(this, 'FieldHandlerFunctionArn', {
      value: fieldHandler.functionArn,
      description: 'ARN of the FieldHandler Lambda function',
      exportName: `${this.stackName}-FieldHandlerArn`,
    });

    new cdk.CfnOutput(this, 'FieldHandlerFunctionName', {
      value: fieldHandler.functionName,
      description: 'Name of the FieldHandler Lambda function',
      exportName: `${this.stackName}-FieldHandlerName`,
    });

    new cdk.CfnOutput(this, 'RelationHandlerFunctionArn', {
      value: relationHandler.functionArn,
      description: 'ARN of the RelationHandler Lambda function',
      exportName: `${this.stackName}-RelationHandlerArn`,
    });

    new cdk.CfnOutput(this, 'RelationHandlerFunctionName', {
      value: relationHandler.functionName,
      description: 'Name of the RelationHandler Lambda function',
      exportName: `${this.stackName}-RelationHandlerName`,
    });

    new cdk.CfnOutput(this, 'RecordHandlerFunctionArn', {
      value: recordHandler.functionArn,
      description: 'ARN of the RecordHandler Lambda function',
      exportName: `${this.stackName}-RecordHandlerArn`,
    });

    new cdk.CfnOutput(this, 'RecordHandlerFunctionName', {
      value: recordHandler.functionName,
      description: 'Name of the RecordHandler Lambda function',
      exportName: `${this.stackName}-RecordHandlerName`,
    });

    new cdk.CfnOutput(this, 'DataSourceHandlerFunctionArn', {
      value: dataSourceHandler.functionArn,
      description: 'ARN of the DataSourceHandler Lambda function',
      exportName: `${this.stackName}-DataSourceHandlerArn`,
    });

    new cdk.CfnOutput(this, 'DataSourceHandlerFunctionName', {
      value: dataSourceHandler.functionName,
      description: 'Name of the DataSourceHandler Lambda function',
      exportName: `${this.stackName}-DataSourceHandlerName`,
    });

    new cdk.CfnOutput(this, 'SearchHandlerFunctionArn', {
      value: searchHandler.functionArn,
      description: 'ARN of the SearchHandler Lambda function',
      exportName: `${this.stackName}-SearchHandlerArn`,
    });

    new cdk.CfnOutput(this, 'SearchHandlerFunctionName', {
      value: searchHandler.functionName,
      description: 'Name of the SearchHandler Lambda function',
      exportName: `${this.stackName}-SearchHandlerName`,
    });

    new cdk.CfnOutput(this, 'ImportExportHandlerFunctionArn', {
      value: importExportHandler.functionArn,
      description: 'ARN of the ImportExportHandler Lambda function',
      exportName: `${this.stackName}-ImportExportHandlerArn`,
    });

    new cdk.CfnOutput(this, 'ImportExportHandlerFunctionName', {
      value: importExportHandler.functionName,
      description: 'Name of the ImportExportHandler Lambda function',
      exportName: `${this.stackName}-ImportExportHandlerName`,
    });

    new cdk.CfnOutput(this, 'FunctionCount', {
      value: String(this.functions.length),
      description:
        'Number of Lambda functions in the Entity Management stack (7)',
      exportName: `${this.stackName}-FunctionCount`,
    });

    // -----------------------------------------------------------------------
    // 7. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------
    // Tags applied at the stack level propagate to all child resources,
    // enabling cost allocation, operational visibility, and automated
    // discovery across the WebVella ERP microservices fleet.

    cdk.Tags.of(this).add('service', 'entity-management');
    cdk.Tags.of(this).add('domain', 'entity-management');
    cdk.Tags.of(this).add(
      'environment',
      isLocalStack ? 'localstack' : 'production'
    );
  }
}
