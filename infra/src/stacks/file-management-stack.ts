/**
 * FileManagementStack — File Management Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the File Management bounded
 * context, replacing the monolith's three file storage backends with a unified
 * S3 + DynamoDB architecture:
 *
 * **Source backends replaced:**
 * - PostgreSQL Large Objects (LO) — default legacy storage in DbFileRepository.cs
 * - Filesystem storage — via `FileSystemStorageFolder` setting in Config.json
 * - Blob storage — via Storage.Net library
 *
 * **Target architecture:**
 * - S3 bucket for all file content (unified single backend)
 * - DynamoDB table for file metadata (replacing the `files` PostgreSQL table)
 * - Presigned URL pattern for uploads/downloads (no Lambda streaming)
 *
 * Resources created:
 *
 * 1. **S3 Bucket** (`webvella-erp-files`) — Unified file storage replacing all
 *    three monolith backends. Versioning enabled for zero data loss (AAP §0.8.1),
 *    block all public access (files served via presigned URLs per AAP §0.8.3),
 *    SSE-S3 encryption at rest, CORS for frontend direct uploads, lifecycle
 *    transition to Infrequent Access after 90 days (production only).
 *
 * 2. **DynamoDB Table** (`webvella-erp-file-metadata`) — Single-table design
 *    storing file metadata (Id, FilePath, ContentType, Size, CreatedOn,
 *    ModifiedOn, CreatedBy). Partition key patterns:
 *    - `FILE#{fileId}` — File metadata by unique identifier
 *    - `FILEPATH#{normalizedPath}` — File metadata by normalized path
 *    Sort key: `META` for file metadata entries.
 *    GSI1: `GSI1PK`/`GSI1SK` for path-based lookups replicating
 *    `DbFileRepository.Find(filepath)` case-insensitive path queries.
 *
 * 3. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    - `webvella-file-management-upload` — S3 presigned URL generation, metadata
 *      creation, multi-file upload, file move operations. Replaces
 *      DbFileRepository.Create(), UserFileService.cs upload logic, and
 *      WebApiController POST /fs/upload/* endpoints.
 *    - `webvella-file-management-download` — File retrieval via presigned URLs
 *      and metadata queries. Replaces DbFileRepository.Find() and
 *      WebApiController GET /fs/* endpoints.
 *
 * 4. **SSM Parameters** — Resource discovery per AAP §0.8.6:
 *    - `/webvella-erp/file-management/bucket-name` → S3 bucket name
 *    - `/webvella-erp/file-management/table-name` → DynamoDB table name
 *
 * Domain events published to the shared SNS event bus:
 * - `file-management.file.uploaded` — New file uploaded to S3
 * - `file-management.file.moved` — File moved/renamed (S3 copy + delete)
 * - `file-management.file.deleted` — File deleted from S3
 *
 * Source files referenced:
 * - WebVella.Erp/Database/DbFileRepository.cs — File lifecycle (LO/FS/blob)
 * - WebVella.Erp/Database/DbFile.cs — File metadata model
 * - WebVella.Erp.Web/Services/UserFileService.cs — User file upload/finalization
 * - WebVella.Erp.Web/Services/FileService.cs — Embedded resource utilities
 *
 * @module infra/src/stacks/file-management-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as s3 from 'aws-cdk-lib/aws-s3';
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
// Interface: FileManagementStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the FileManagementStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface FileManagementStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - S3 removal policy: DESTROY (LocalStack) vs RETAIN (production)
   * - S3 auto-delete objects: enabled (LocalStack) vs disabled (production)
   * - S3 lifecycle rules: skipped (LocalStack) vs 90-day IA transition (production)
   * - DynamoDB removal policy: DESTROY vs RETAIN
   * - AWS_ENDPOINT_URL injection for SDK redirects
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The UploadHandler Lambda publishes domain events
   * to this topic using the naming convention from AAP §0.8.5:
   * - `file-management.file.uploaded`
   * - `file-management.file.moved`
   * - `file-management.file.deleted`
   *
   * Replaces the monolith's synchronous post-hook invocations for file
   * lifecycle events from DbFileRepository CRUD operations.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: FileManagementStack
// ---------------------------------------------------------------------------

/**
 * FileManagementStack — CDK stack for the File Management bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own S3 bucket,
 * DynamoDB table, Lambda functions, IAM policies, and SSM parameters. No
 * other service may directly access the file management datastore.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack for
 * route-to-Lambda integration mapping and cross-stack resource discovery:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `bucketName` — S3 bucket name for file storage
 * - `tableName` — DynamoDB table name for file metadata
 *
 * @example
 * ```typescript
 * const fileManagementStack = new FileManagementStack(app, 'FileManagementStack', {
 *   isLocalStack: true,
 *   eventBus: sharedStack.eventBus,
 *   env: { account: '000000000000', region: 'us-east-1' },
 * });
 * ```
 */
export class FileManagementStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the UploadHandler and DownloadHandler functions that handle
   * all file management HTTP endpoints. Consumed by ApiGatewayStack for
   * path-based routing under `/v1/files/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * S3 bucket name for file content storage.
   *
   * The bucket stores all uploaded files, replacing the monolith's three
   * storage backends (PostgreSQL LO, filesystem, blob via Storage.Net).
   * Also published as SSM parameter at `/webvella-erp/file-management/bucket-name`.
   */
  public readonly bucketName: string;

  /**
   * DynamoDB table name for file metadata storage.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/file-management/table-name`.
   */
  public readonly tableName: string;

  constructor(scope: Construct, id: string, props: FileManagementStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. S3 Bucket — Unified file content storage
    // -----------------------------------------------------------------------
    // Replaces DbFileRepository.cs three storage backends:
    //   - PostgreSQL Large Objects (default, via lo_create/lo_open/lo_write)
    //   - Filesystem storage (via FileSystemStorageFolder config setting)
    //   - Blob storage (via Storage.Net IBlobStorage)
    //
    // All file content now stored in a single S3 bucket with:
    //   - Versioning for zero data loss (AAP §0.8.1)
    //   - All public access blocked (files served via presigned URLs)
    //   - SSE-S3 encryption at rest (AAP §0.8.3)
    //   - CORS for direct frontend uploads via presigned URLs
    //   - Lifecycle transition to Infrequent Access after 90 days (production)
    //
    // File path convention preserved from monolith:
    //   DbFileRepository.FOLDER_SEPARATOR = "/"
    //   DbFileRepository.TMP_FOLDER_NAME = "tmp"
    //   UserFileService: /file/{newFileId}/{fileName}

    const fileBucket = new s3.Bucket(this, 'FileStorageBucket', {
      // Block ALL public access — files served exclusively via presigned URLs
      // generated by Lambda handlers. This replaces the monolith's direct
      // filesystem serving and ensures all access is authenticated and auditable
      // per AAP §0.8.3 security requirements.
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,

      // S3-managed encryption at rest (SSE-S3) per AAP §0.8.3:
      // "Encryption at rest for all datastores (DynamoDB, RDS, S3)"
      encryption: s3.BucketEncryption.S3_MANAGED,

      // Versioning enabled for zero data loss per AAP §0.8.1:
      // "Zero data loss during migration. Existing data in the PostgreSQL
      // monolith must be migrateable to the per-service datastores."
      // Versioning also enables point-in-time recovery of accidentally
      // deleted or overwritten files.
      versioned: true,

      // Removal policy conditional on deployment target per AAP §0.7.6:
      // DESTROY for LocalStack enables clean teardown during development
      // with `cdklocal destroy`. RETAIN for production prevents accidental
      // data loss during stack updates or redeployments.
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,

      // Auto-delete objects only in LocalStack mode for clean teardown.
      // When enabled, a custom resource Lambda deletes all objects before
      // the bucket itself is deleted. Never enabled in production to
      // prevent catastrophic data loss.
      autoDeleteObjects: isLocalStack,

      // CORS configuration for direct frontend uploads via presigned URLs.
      // The React SPA generates presigned PUT URLs via the UploadHandler
      // Lambda, then uploads directly from the browser to S3.
      // This replaces the monolith's multipart form upload through
      // WebApiController POST /fs/upload/ endpoints.
      cors: [
        {
          allowedMethods: [
            s3.HttpMethods.GET,
            s3.HttpMethods.PUT,
            s3.HttpMethods.POST,
            s3.HttpMethods.DELETE,
          ],
          allowedOrigins: ['*'],
          allowedHeaders: [
            '*',
          ],
          exposedHeaders: [
            'ETag',
            'x-amz-request-id',
            'x-amz-id-2',
          ],
          maxAge: 3600,
        },
      ],

      // Lifecycle rules for storage cost optimization in production.
      // Transition files to Infrequent Access after 90 days since most
      // ERP files are actively accessed within the first few months.
      // Skipped in LocalStack since cost optimization is irrelevant
      // in local development and IA storage class is not fully emulated.
      lifecycleRules: isLocalStack
        ? []
        : [
            {
              id: 'TransitionToInfrequentAccess',
              enabled: true,
              transitions: [
                {
                  storageClass: s3.StorageClass.INFREQUENT_ACCESS,
                  transitionAfter: cdk.Duration.days(90),
                },
              ],
            },
            {
              id: 'CleanupNoncurrentVersions',
              enabled: true,
              noncurrentVersionExpiration: cdk.Duration.days(90),
            },
          ],
    });

    // -----------------------------------------------------------------------
    // 2. DynamoDB Table — File metadata storage
    // -----------------------------------------------------------------------
    // Replaces the PostgreSQL `files` table from DbFileRepository.cs.
    //
    // Single-table design with the following access patterns:
    //   PK=FILE#{fileId},                 SK=META  → File metadata by ID
    //   PK=FILEPATH#{normalizedPath},     SK=META  → File by normalized path
    //
    // GSI1 enables path-based lookups replicating DbFileRepository.Find():
    //   GSI1PK=FOLDER#{folderPath},  GSI1SK=NAME#{fileName}  → Files in folder
    //   GSI1PK=ENTITY#{entityName},  GSI1SK=FILE#{fileId}    → Files for entity
    //
    // Metadata fields (from DbFile.cs):
    //   Id (Guid), ObjectId (uint auto-increment), FilePath (string),
    //   ContentType (string), Size (long), CreatedBy (Guid?),
    //   CreatedOn (DateTime), LastModifiedBy (Guid?),
    //   LastModificationDate (DateTime), S3Key (string), S3VersionId (string)

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

    const fileMetadataTable = new WebVellaDynamoDBTable(this, 'FileMetadataTable', {
      serviceName: 'file-management',
      tableName: 'file-metadata',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 3. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // S3 read/write permissions for the UploadHandler Lambda.
    // Covers: presigned URL generation (PutObject, GetObject), file move
    // (CopyObject + DeleteObject), and multi-file operations.
    // Scoped to the file storage bucket only.
    const s3ReadWritePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        's3:PutObject',
        's3:GetObject',
        's3:CopyObject',
        's3:DeleteObject',
        's3:ListBucket',
      ],
      resources: [
        fileBucket.bucketArn,
        `${fileBucket.bucketArn}/*`,
      ],
    });

    // S3 read-only permissions for the DownloadHandler Lambda.
    // Only needs GetObject for presigned URL generation and HeadObject
    // for metadata retrieval. Scoped to the file storage bucket only.
    const s3ReadOnlyPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        's3:GetObject',
        's3:HeadObject',
        's3:ListBucket',
      ],
      resources: [
        fileBucket.bucketArn,
        `${fileBucket.bucketArn}/*`,
      ],
    });

    // DynamoDB CRUD permissions scoped to the file metadata table and its GSIs.
    // Used by both Lambda functions for metadata operations.
    const dynamoDbCrudPolicy = new iam.PolicyStatement({
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
        fileMetadataTable.tableArn,
        `${fileMetadataTable.tableArn}/index/*`,
      ],
    });

    // DynamoDB read-only permissions for the DownloadHandler Lambda.
    // Only needs GetItem and Query for metadata retrieval.
    const dynamoDbReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'dynamodb:GetItem',
        'dynamodb:Query',
        'dynamodb:Scan',
        'dynamodb:BatchGetItem',
      ],
      resources: [
        fileMetadataTable.tableArn,
        `${fileMetadataTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // The UploadHandler publishes domain events for file lifecycle actions.
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
    // 4. Lambda Functions — .NET 9 Native AOT handlers
    // -----------------------------------------------------------------------

    // 4a. UploadHandler — File upload, move, and delete operations
    // Handles HTTP endpoints:
    //   POST   /v1/files/upload          → Generate presigned URL + create metadata
    //   POST   /v1/files/upload-multiple → Multi-file presigned URLs
    //   POST   /v1/files/move            → S3 copy + delete (file rename/move)
    //   DELETE /v1/files/{*filepath}     → S3 delete + DynamoDB metadata delete
    //
    // Source mapping:
    //   DbFileRepository.Create()         → S3 PutObject via presigned URL
    //   DbFileRepository.Move()           → S3 CopyObject + DeleteObject
    //   DbFileRepository.Delete()         → S3 DeleteObject
    //   UserFileService.UploadFile()      → Presigned URL + metadata record
    //   UserFileService.FinalizeUpload()  → Move from tmp/ to final path
    //
    // Publishes domain events:
    //   file-management.file.uploaded — after successful upload + metadata creation
    //   file-management.file.moved    — after successful file move/rename
    //   file-management.file.deleted  — after successful file deletion

    const uploadHandlerEnv: Record<string, string> = {
      TABLE_NAME: fileMetadataTable.tableName,
      BUCKET_NAME: fileBucket.bucketName,
      EVENT_TOPIC_ARN: eventBus.topicArn,
    };

    // Inject AWS_ENDPOINT_URL for LocalStack SDK redirect
    if (isLocalStack) {
      uploadHandlerEnv['AWS_ENDPOINT_URL'] = 'http://localhost:4566';
    }

    const uploadHandler = new WebVellaLambdaService(this, 'UploadHandler', {
      serviceName: 'file-management',
      functionName: 'upload',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/file-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'File Management upload handler — presigned URL generation, metadata creation, ' +
        'multi-file upload, file move/rename, and file deletion. Replaces ' +
        'DbFileRepository CRUD and UserFileService upload logic. Publishes ' +
        'file-management.file.{uploaded,moved,deleted} domain events.',
      environment: uploadHandlerEnv,
      additionalPolicies: [s3ReadWritePolicy, dynamoDbCrudPolicy, snsPublishPolicy],
    });

    // 4b. DownloadHandler — File retrieval and metadata queries
    // Handles HTTP endpoints:
    //   GET /v1/files/{*filepath}       → Generate presigned download URL
    //   GET /v1/files/metadata/{fileId} → File metadata retrieval
    //   GET /v1/files/list              → List files (with pagination)
    //
    // Source mapping:
    //   DbFileRepository.Find(filepath)          → DynamoDB Query on GSI1
    //   DbFileRepository.FindAll(startsWithPath)  → DynamoDB Query with begins_with
    //   WebApiController GET /fs/{fileName}      → S3 presigned URL generation

    const downloadHandlerEnv: Record<string, string> = {
      TABLE_NAME: fileMetadataTable.tableName,
      BUCKET_NAME: fileBucket.bucketName,
    };

    // Inject AWS_ENDPOINT_URL for LocalStack SDK redirect
    if (isLocalStack) {
      downloadHandlerEnv['AWS_ENDPOINT_URL'] = 'http://localhost:4566';
    }

    const downloadHandler = new WebVellaLambdaService(this, 'DownloadHandler', {
      serviceName: 'file-management',
      functionName: 'download',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/file-management/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'File Management download handler — presigned download URL generation and ' +
        'file metadata queries. Replaces DbFileRepository.Find() path-based lookup ' +
        'and WebApiController GET /fs/* endpoints.',
      environment: downloadHandlerEnv,
      additionalPolicies: [s3ReadOnlyPolicy, dynamoDbReadPolicy],
    });

    // -----------------------------------------------------------------------
    // 5. SSM Parameters — Resource names for cross-service discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use these parameters to locate
    // the file management service's storage resources without hardcoded names.

    new ssm.StringParameter(this, 'BucketNameParam', {
      parameterName: '/webvella-erp/file-management/bucket-name',
      stringValue: fileBucket.bucketName,
      description:
        'S3 bucket name for the File Management service file storage. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    new ssm.StringParameter(this, 'TableNameParam', {
      parameterName: '/webvella-erp/file-management/table-name',
      stringValue: fileMetadataTable.tableName,
      description:
        'DynamoDB table name for the File Management service metadata store. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    // -----------------------------------------------------------------------
    // 6. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [uploadHandler.function, downloadHandler.function];
    this.bucketName = fileBucket.bucketName;
    this.tableName = fileMetadataTable.tableName;

    // -----------------------------------------------------------------------
    // 7. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'FileStorageBucketName', {
      value: fileBucket.bucketName,
      description: 'S3 bucket name for file content storage',
      exportName: `${this.stackName}-BucketName`,
    });

    new cdk.CfnOutput(this, 'FileMetadataTableName', {
      value: fileMetadataTable.tableName,
      description: 'DynamoDB table name for file metadata',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'UploadHandlerFunctionArn', {
      value: uploadHandler.functionArn,
      description: 'ARN of the File Management upload handler Lambda function',
      exportName: `${this.stackName}-UploadHandlerArn`,
    });

    new cdk.CfnOutput(this, 'UploadHandlerFunctionName', {
      value: uploadHandler.functionName,
      description: 'Name of the File Management upload handler Lambda function',
      exportName: `${this.stackName}-UploadHandlerName`,
    });

    new cdk.CfnOutput(this, 'DownloadHandlerFunctionArn', {
      value: downloadHandler.functionArn,
      description: 'ARN of the File Management download handler Lambda function',
      exportName: `${this.stackName}-DownloadHandlerArn`,
    });

    new cdk.CfnOutput(this, 'DownloadHandlerFunctionName', {
      value: downloadHandler.functionName,
      description: 'Name of the File Management download handler Lambda function',
      exportName: `${this.stackName}-DownloadHandlerName`,
    });

    // -----------------------------------------------------------------------
    // 8. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', 'file-management');
    cdk.Tags.of(this).add('domain', 'file-management');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
