using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebVellaErp.FileManagement.Models;

namespace WebVellaErp.FileManagement.DataAccess;

/// <summary>
/// Contract for DynamoDB-backed file metadata persistence.
/// Replaces all PostgreSQL-based file metadata operations from
/// <c>DbFileRepository</c> (WebVella.Erp/Database/DbFileRepository.cs, 513 lines).
/// All methods are async with cooperative cancellation support for Lambda execution.
/// </summary>
public interface IFileMetadataRepository
{
    /// <summary>
    /// Finds file metadata by exact filepath match.
    /// Replaces <c>DbFileRepository.Find(string filepath)</c> (source lines 34-56).
    /// Uses DynamoDB GSI query on filepath-index for efficient lookup.
    /// </summary>
    /// <param name="filepath">The file path to search for (will be normalized).</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The file metadata if found; otherwise <c>null</c>.</returns>
    Task<FileMetadata?> FindByFilePathAsync(string filepath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds file metadata by unique file identifier.
    /// New method — the monolith always looked up by filepath.
    /// Uses DynamoDB GetItem with strong consistency.
    /// </summary>
    /// <param name="fileId">The unique file identifier.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The file metadata if found; otherwise <c>null</c>.</returns>
    Task<FileMetadata?> FindByIdAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all file metadata with optional path prefix and temp file filtering.
    /// Replaces <c>DbFileRepository.FindAll()</c> (source lines 58-117).
    /// Uses cursor-based pagination (DynamoDB LastEvaluatedKey) instead of SQL OFFSET/LIMIT.
    /// </summary>
    /// <param name="startsWithPath">Optional path prefix filter (normalized, begins_with query).</param>
    /// <param name="includeTempFiles">Whether to include temporary files (default false).</param>
    /// <param name="pageSize">Maximum number of items to return per page.</param>
    /// <param name="exclusiveStartKey">Pagination cursor from previous call.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>Tuple of matching items and next-page cursor token (null if no more pages).</returns>
    Task<(List<FileMetadata> Items, string? LastEvaluatedKey)> FindAllAsync(
        string? startsWithPath = null,
        bool includeTempFiles = false,
        int? pageSize = null,
        string? exclusiveStartKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new file metadata record in DynamoDB.
    /// Replaces <c>DbFileRepository.Create()</c> (source lines 119-200).
    /// Uses conditional PutItem to prevent duplicates (idempotent write).
    /// File content upload to S3 is NOT handled here — that is S3Service's responsibility.
    /// </summary>
    /// <param name="metadata">The file metadata to persist.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The persisted file metadata.</returns>
    Task<FileMetadata> CreateAsync(FileMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a full replacement update of file metadata.
    /// Uses conditional PutItem to ensure item exists before update.
    /// </summary>
    /// <param name="metadata">The updated file metadata.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The updated file metadata.</returns>
    Task<FileMetadata> UpdateAsync(FileMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the modification date for a file identified by filepath.
    /// Replaces <c>DbFileRepository.UpdateModificationDate()</c> (source lines 202-225).
    /// </summary>
    /// <param name="filepath">The file path to update (will be normalized).</param>
    /// <param name="modificationDate">The new modification timestamp (UTC).</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The updated file metadata if found; otherwise throws.</returns>
    Task<FileMetadata?> UpdateModificationDateAsync(string filepath, DateTime modificationDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves file metadata from source path to destination path (updates filepath attribute).
    /// Replaces <c>DbFileRepository.Move()</c> (source lines 290-368).
    /// S3 object move (copy+delete) is NOT handled here — that is S3Service's responsibility.
    /// </summary>
    /// <param name="sourceFilepath">Source file path.</param>
    /// <param name="destinationFilepath">Destination file path.</param>
    /// <param name="overwrite">Whether to overwrite existing file at destination.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The updated file metadata at the destination path.</returns>
    Task<FileMetadata?> MoveAsync(string sourceFilepath, string destinationFilepath, bool overwrite = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies file metadata from source to destination (creates new metadata record).
    /// Replaces <c>DbFileRepository.Copy()</c> (source lines 234-281).
    /// S3 object copy is NOT handled here — that is S3Service's responsibility.
    /// </summary>
    /// <param name="sourceFilepath">Source file path.</param>
    /// <param name="destinationFilepath">Destination file path.</param>
    /// <param name="overwrite">Whether to overwrite existing file at destination.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The newly created file metadata at the destination path.</returns>
    Task<FileMetadata?> CopyMetadataAsync(string sourceFilepath, string destinationFilepath, bool overwrite = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes file metadata by filepath.
    /// Replaces <c>DbFileRepository.Delete()</c> (source lines 375-429).
    /// Returns silently if file not found (matching source behavior at line 388).
    /// S3 object deletion is NOT handled here — that is S3Service's responsibility.
    /// </summary>
    /// <param name="filepath">The file path to delete (will be normalized).</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    Task DeleteAsync(string filepath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes file metadata by unique file identifier.
    /// No-op if item does not exist (matching Delete() behavior).
    /// </summary>
    /// <param name="fileId">The unique file identifier to delete.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    Task DeleteByIdAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates temporary file metadata with auto-expiry via DynamoDB TTL.
    /// Replaces <c>DbFileRepository.CreateTempFile()</c> (source lines 437-449).
    /// DynamoDB TTL replaces <c>CleanupExpiredTempFiles()</c> cron job (source lines 455-469).
    /// File content is NOT handled here — S3 presigned URL handles upload.
    /// </summary>
    /// <param name="filename">The filename (without path).</param>
    /// <param name="extension">Optional file extension (e.g., ".png").</param>
    /// <param name="contentType">MIME content type.</param>
    /// <param name="size">File size in bytes.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
    /// <returns>The created temporary file metadata with TTL set.</returns>
    Task<FileMetadata> CreateTempFileMetadataAsync(string filename, string? extension, string contentType, long size, CancellationToken cancellationToken = default);
}

/// <summary>
/// DynamoDB-backed file metadata repository implementing single-table design.
/// Replaces <c>DbFileRepository</c> from the monolith (513 lines of PostgreSQL persistence).
/// Uses low-level DynamoDB SDK operations for full control over key design, GSI queries,
/// conditional expressions, and TTL-based temp file auto-expiry.
/// <para>
/// DynamoDB Single-Table Design:
///   PK = FILE#{fileId}  (partition key)
///   SK = META            (sort key — constant, enables future expansion for versions/tags)
///   GSI filepath-index: hash key = filepath (enables exact match and begins_with queries)
/// </para>
/// </summary>
public class FileMetadataRepository : IFileMetadataRepository
{
    // ---------------------------------------------------------------------------
    // DynamoDB single-table design constants
    // ---------------------------------------------------------------------------

    /// <summary>Environment variable name for overriding the DynamoDB table name.</summary>
    private const string TableNameEnv = "FILE_MANAGEMENT_TABLE_NAME";

    /// <summary>Default DynamoDB table name when environment variable is not set.</summary>
    private const string DefaultTableName = "file-management-files";

    /// <summary>Partition key attribute name in DynamoDB.</summary>
    private const string PkAttr = "PK";

    /// <summary>Sort key attribute name in DynamoDB.</summary>
    private const string SkAttr = "SK";

    /// <summary>Constant sort key value for file metadata items.</summary>
    private const string SkValue = "META";

    /// <summary>Partition key prefix for file metadata items.</summary>
    private const string PkPrefix = "FILE#";

    /// <summary>Name of the GSI used for filepath-based lookups.</summary>
    private const string FilepathIndexName = "filepath-index";

    /// <summary>Attribute name used as the GSI hash key.</summary>
    private const string FilepathAttr = "filepath";

    // DynamoDB item attribute names (camelCase per service convention)
    private const string IdAttr = "id";
    private const string ObjectKeyAttr = "objectKey";
    private const string ContentTypeAttr = "contentType";
    private const string SizeAttr = "size";
    private const string CreatedByAttr = "createdBy";
    private const string CreatedOnAttr = "createdOn";
    private const string LastModifiedByAttr = "lastModifiedBy";
    private const string LastModificationDateAttr = "lastModificationDate";
    private const string IsTempAttr = "isTemp";
    private const string TtlAttr = "ttl";

    // ---------------------------------------------------------------------------
    // Constants from source DbFileRepository.cs (lines 14-15)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Folder path separator character. Matches <c>DbFileRepository.FOLDER_SEPARATOR</c>
    /// (source line 14) and <c>FileMetadata.FolderSeparator</c>.
    /// </summary>
    public const string FOLDER_SEPARATOR = "/";

    /// <summary>
    /// Temporary folder name. Matches <c>DbFileRepository.TMP_FOLDER_NAME</c>
    /// (source line 15) and <c>FileMetadata.TmpFolderName</c>.
    /// </summary>
    public const string TMP_FOLDER_NAME = "tmp";

    // ---------------------------------------------------------------------------
    // Private fields
    // ---------------------------------------------------------------------------
    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly ILogger<FileMetadataRepository> _logger;
    private readonly string _tableName;
    private readonly int _defaultTempFileTtlHours;

    // ---------------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Initializes a new instance of <see cref="FileMetadataRepository"/>.
    /// Replaces the monolith's <c>DbContext</c>/<c>DbConnection</c>-based constructor pattern
    /// with DI-injected DynamoDB client, structured logger, and configuration.
    /// <para>
    /// The <paramref name="dynamoDbClient"/> automatically respects <c>AWS_ENDPOINT_URL</c>
    /// environment variable for LocalStack compatibility (AAP §0.8.6). No endpoint URLs
    /// are hardcoded in this repository.
    /// </para>
    /// </summary>
    /// <param name="dynamoDbClient">AWS DynamoDB client (auto-configured with AWS_ENDPOINT_URL).</param>
    /// <param name="logger">Structured JSON logger for correlation-ID propagation (AAP §0.8.5).</param>
    /// <param name="configuration">Configuration for table name and TTL overrides.</param>
    public FileMetadataRepository(
        IAmazonDynamoDB dynamoDbClient,
        ILogger<FileMetadataRepository> logger,
        IConfiguration configuration)
    {
        _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(configuration);
        // Use trim/AOT-safe string indexer instead of GetValue<T>() to avoid IL2026
        _tableName = configuration[TableNameEnv] ?? DefaultTableName;
        var ttlConfig = configuration["TEMP_FILE_TTL_HOURS"];
        _defaultTempFileTtlHours = int.TryParse(ttlConfig, out var parsedTtl) ? parsedTtl : 24;
    }

    // ---------------------------------------------------------------------------
    // IFileMetadataRepository — Read Operations
    // ---------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<FileMetadata?> FindByFilePathAsync(
        string filepath,
        CancellationToken cancellationToken = default)
    {
        ValidateFilePath(filepath, nameof(filepath));
        var normalizedPath = FileMetadata.NormalizeFilePath(filepath);

        _logger.LogInformation(
            "Finding file metadata by filepath. Filepath={FilePath}",
            normalizedPath);

        try
        {
            var request = new QueryRequest
            {
                TableName = _tableName,
                IndexName = FilepathIndexName,
                KeyConditionExpression = $"{FilepathAttr} = :fp",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":fp", new AttributeValue { S = normalizedPath } }
                },
                Limit = 1
            };

            var response = await _dynamoDbClient
                .QueryAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.Items.Count == 0)
            {
                _logger.LogInformation(
                    "File metadata not found by filepath. Filepath={FilePath}",
                    normalizedPath);
                return null;
            }

            return FromAttributeMap(response.Items[0]);
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error finding file by filepath. Filepath={FilePath}, ErrorCode={ErrorCode}",
                normalizedPath,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata?> FindByIdAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Finding file metadata by ID. FileId={FileId}",
            fileId);

        try
        {
            var request = new GetItemRequest
            {
                TableName = _tableName,
                Key = BuildPrimaryKey(fileId),
                ConsistentRead = true
            };

            var response = await _dynamoDbClient
                .GetItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.Item == null || response.Item.Count == 0)
            {
                _logger.LogInformation(
                    "File metadata not found by ID. FileId={FileId}",
                    fileId);
                return null;
            }

            return FromAttributeMap(response.Item);
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error finding file by ID. FileId={FileId}, ErrorCode={ErrorCode}",
                fileId,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<(List<FileMetadata> Items, string? LastEvaluatedKey)> FindAllAsync(
        string? startsWithPath = null,
        bool includeTempFiles = false,
        int? pageSize = null,
        string? exclusiveStartKey = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Finding all file metadata. StartsWithPath={StartsWithPath}, IncludeTempFiles={IncludeTempFiles}, PageSize={PageSize}",
            startsWithPath,
            includeTempFiles,
            pageSize);

        try
        {
            // Build filter expression dynamically based on parameters
            // Always filter on SK = META to exclude non-metadata items in single-table design
            var filterParts = new List<string> { $"{SkAttr} = :sk" };
            var expressionValues = new Dictionary<string, AttributeValue>
            {
                { ":sk", new AttributeValue { S = SkValue } }
            };

            // Path prefix filtering — replaces SQL ILIKE @startswith (source lines 86-97)
            if (!string.IsNullOrWhiteSpace(startsWithPath))
            {
                var normalizedPrefix = FileMetadata.NormalizeFilePath(startsWithPath);
                filterParts.Add($"begins_with({FilepathAttr}, :prefix)");
                expressionValues[":prefix"] = new AttributeValue { S = normalizedPrefix };
            }

            // Temp file exclusion — replaces SQL NOT ILIKE %/tmp% (source lines 86-103)
            if (!includeTempFiles)
            {
                filterParts.Add($"{IsTempAttr} = :notTemp");
                expressionValues[":notTemp"] = new AttributeValue { BOOL = false };
            }

            var request = new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = string.Join(" AND ", filterParts),
                ExpressionAttributeValues = expressionValues
            };

            // Cursor-based pagination replaces SQL LIMIT/OFFSET (source lines 69-80)
            if (pageSize.HasValue && pageSize.Value > 0)
            {
                request.Limit = pageSize.Value;
            }

            // Deserialize pagination cursor from previous call
            if (!string.IsNullOrWhiteSpace(exclusiveStartKey))
            {
                request.ExclusiveStartKey = DeserializeExclusiveStartKey(exclusiveStartKey);
            }

            var response = await _dynamoDbClient
                .ScanAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var items = new List<FileMetadata>(response.Items.Count);
            foreach (var item in response.Items)
            {
                items.Add(FromAttributeMap(item));
            }

            var nextPageToken = SerializeLastEvaluatedKey(response.LastEvaluatedKey);

            _logger.LogInformation(
                "Found {Count} file metadata items. HasMorePages={HasMore}",
                items.Count,
                nextPageToken != null);

            return (items, nextPageToken);
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error scanning file metadata. ErrorCode={ErrorCode}",
                ex.ErrorCode);
            throw;
        }
    }

    // ---------------------------------------------------------------------------
    // IFileMetadataRepository — Write Operations
    // ---------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<FileMetadata> CreateAsync(
        FileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateFilePath(metadata.FilePath, nameof(metadata.FilePath));

        // Normalize filepath (source lines 124-127)
        metadata.FilePath = FileMetadata.NormalizeFilePath(metadata.FilePath);

        _logger.LogInformation(
            "Creating file metadata. FileId={FileId}, FilePath={FilePath}",
            metadata.Id,
            metadata.FilePath);

        // Check for existing file by filepath — source lines 129-130:
        // throws ArgumentException("{filepath}: file already exists") if found
        var existing = await FindByFilePathAsync(metadata.FilePath, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            throw new ArgumentException($"{metadata.FilePath}: file already exists");
        }

        try
        {
            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = ToAttributeMap(metadata),
                // Conditional expression prevents duplicate PK (optimistic create, idempotent)
                ConditionExpression = $"attribute_not_exists({PkAttr})"
            };

            await _dynamoDbClient
                .PutItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Successfully created file metadata. FileId={FileId}, FilePath={FilePath}",
                metadata.Id,
                metadata.FilePath);

            return metadata;
        }
        catch (ConditionalCheckFailedException)
        {
            _logger.LogWarning(
                "Duplicate file metadata detected (conditional check failed). FileId={FileId}, FilePath={FilePath}",
                metadata.Id,
                metadata.FilePath);
            throw new ArgumentException($"{metadata.FilePath}: file already exists");
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error creating file metadata. FileId={FileId}, FilePath={FilePath}, ErrorCode={ErrorCode}",
                metadata.Id,
                metadata.FilePath,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata> UpdateAsync(
        FileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateFilePath(metadata.FilePath, nameof(metadata.FilePath));

        metadata.FilePath = FileMetadata.NormalizeFilePath(metadata.FilePath);

        _logger.LogInformation(
            "Updating file metadata. FileId={FileId}, FilePath={FilePath}",
            metadata.Id,
            metadata.FilePath);

        try
        {
            var request = new PutItemRequest
            {
                TableName = _tableName,
                Item = ToAttributeMap(metadata),
                // Ensures item exists before replacement (no silent upsert)
                ConditionExpression = $"attribute_exists({PkAttr})"
            };

            await _dynamoDbClient
                .PutItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Successfully updated file metadata. FileId={FileId}, FilePath={FilePath}",
                metadata.Id,
                metadata.FilePath);

            return metadata;
        }
        catch (ConditionalCheckFailedException)
        {
            _logger.LogWarning(
                "File metadata not found for update. FileId={FileId}",
                metadata.Id);
            throw new ArgumentException("file does not exist");
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error updating file metadata. FileId={FileId}, ErrorCode={ErrorCode}",
                metadata.Id,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata?> UpdateModificationDateAsync(
        string filepath,
        DateTime modificationDate,
        CancellationToken cancellationToken = default)
    {
        // Validate and normalize — source lines 204-210
        ValidateFilePath(filepath, nameof(filepath));
        var normalizedPath = FileMetadata.NormalizeFilePath(filepath);

        _logger.LogInformation(
            "Updating modification date. FilePath={FilePath}, ModificationDate={ModDate}",
            normalizedPath,
            modificationDate.ToString("O"));

        // Find file by filepath — source lines 214-216: throws if not found
        var file = await FindByFilePathAsync(normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        if (file == null)
        {
            throw new ArgumentException("file does not exist");
        }

        try
        {
            // Atomic update of modification date — source lines 218-221
            var request = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = BuildPrimaryKey(file.Id),
                UpdateExpression = $"SET {LastModificationDateAttr} = :modDate",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":modDate", new AttributeValue { S = modificationDate.ToString("O") } }
                },
                ConditionExpression = $"attribute_exists({PkAttr})",
                ReturnValues = ReturnValue.ALL_NEW
            };

            var response = await _dynamoDbClient
                .UpdateItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var updated = FromAttributeMap(response.Attributes);

            _logger.LogInformation(
                "Successfully updated modification date. FileId={FileId}, FilePath={FilePath}",
                updated.Id,
                updated.FilePath);

            return updated;
        }
        catch (ConditionalCheckFailedException)
        {
            _logger.LogWarning(
                "File metadata not found for modification date update. FilePath={FilePath}",
                normalizedPath);
            throw new ArgumentException("file does not exist");
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error updating modification date. FilePath={FilePath}, ErrorCode={ErrorCode}",
                normalizedPath,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata?> MoveAsync(
        string sourceFilepath,
        string destinationFilepath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        // Validate — source lines 292-296 (exact error messages preserved)
        if (string.IsNullOrWhiteSpace(sourceFilepath))
        {
            throw new ArgumentException("sourceFilepath cannot be null or empty");
        }
        if (string.IsNullOrWhiteSpace(destinationFilepath))
        {
            throw new ArgumentException("destinationFilepath cannot be null or empty");
        }

        // Normalize — source lines 298-305
        var normalizedSource = FileMetadata.NormalizeFilePath(sourceFilepath);
        var normalizedDest = FileMetadata.NormalizeFilePath(destinationFilepath);

        _logger.LogInformation(
            "Moving file metadata. Source={Source}, Destination={Destination}, Overwrite={Overwrite}",
            normalizedSource,
            normalizedDest,
            overwrite);

        // Find source — source line 310: throws if not found
        var srcFile = await FindByFilePathAsync(normalizedSource, cancellationToken)
            .ConfigureAwait(false);
        if (srcFile == null)
        {
            throw new Exception("Source file cannot be found.");
        }

        // Find destination — source line 313: throws if exists and no overwrite
        var destFile = await FindByFilePathAsync(normalizedDest, cancellationToken)
            .ConfigureAwait(false);
        if (destFile != null && !overwrite)
        {
            throw new Exception("Destination file already exists and no overwrite specified.");
        }

        // Delete destination if overwrite — source lines 322-323
        if (destFile != null)
        {
            await DeleteByIdAsync(destFile.Id, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Determine if new path is temporary
            bool isNewTemp = normalizedDest.StartsWith(
                $"{FileMetadata.FolderSeparator}{FileMetadata.TmpFolderName}{FileMetadata.FolderSeparator}",
                StringComparison.OrdinalIgnoreCase);

            // Update filepath in-place on existing record — source lines 325-328
            var updateExprParts = new List<string>
            {
                $"{FilepathAttr} = :newPath",
                $"{IsTempAttr} = :isTemp",
                $"{LastModificationDateAttr} = :modDate"
            };

            var exprValues = new Dictionary<string, AttributeValue>
            {
                { ":newPath", new AttributeValue { S = normalizedDest } },
                { ":isTemp", new AttributeValue { BOOL = isNewTemp } },
                { ":modDate", new AttributeValue { S = DateTime.UtcNow.ToString("O") } }
            };

            // If moving from temp to permanent, clear TTL; if moving to temp, set TTL
            if (isNewTemp && !srcFile.IsTemp)
            {
                updateExprParts.Add($"{TtlAttr} = :ttl");
                exprValues[":ttl"] = new AttributeValue
                {
                    N = DateTimeOffset.UtcNow.AddHours(_defaultTempFileTtlHours).ToUnixTimeSeconds().ToString()
                };
            }
            else if (!isNewTemp && srcFile.IsTemp)
            {
                // Remove TTL when moving from temp to permanent
                updateExprParts.Add($"{TtlAttr} = :ttlRemove");
                exprValues[":ttlRemove"] = new AttributeValue { NULL = true };
            }

            var request = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = BuildPrimaryKey(srcFile.Id),
                UpdateExpression = "SET " + string.Join(", ", updateExprParts),
                ExpressionAttributeValues = exprValues,
                ConditionExpression = $"attribute_exists({PkAttr})",
                ReturnValues = ReturnValue.ALL_NEW
            };

            var response = await _dynamoDbClient
                .UpdateItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            var updated = FromAttributeMap(response.Attributes);

            _logger.LogInformation(
                "Successfully moved file metadata. FileId={FileId}, From={Source}, To={Destination}",
                updated.Id,
                normalizedSource,
                normalizedDest);

            return updated;
        }
        catch (ConditionalCheckFailedException)
        {
            _logger.LogWarning(
                "Source file metadata disappeared during move. Source={Source}",
                normalizedSource);
            throw new Exception("Source file cannot be found.");
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error moving file metadata. Source={Source}, Destination={Destination}, ErrorCode={ErrorCode}",
                normalizedSource,
                normalizedDest,
                ex.ErrorCode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata?> CopyMetadataAsync(
        string sourceFilepath,
        string destinationFilepath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        // Validate — source lines 236-240 (exact error messages preserved)
        if (string.IsNullOrWhiteSpace(sourceFilepath))
        {
            throw new ArgumentException("sourceFilepath cannot be null or empty");
        }
        if (string.IsNullOrWhiteSpace(destinationFilepath))
        {
            throw new ArgumentException("destinationFilepath cannot be null or empty");
        }

        // Normalize — source lines 242-249
        var normalizedSource = FileMetadata.NormalizeFilePath(sourceFilepath);
        var normalizedDest = FileMetadata.NormalizeFilePath(destinationFilepath);

        _logger.LogInformation(
            "Copying file metadata. Source={Source}, Destination={Destination}, Overwrite={Overwrite}",
            normalizedSource,
            normalizedDest,
            overwrite);

        // Find source — source line 255: throws if not found
        var srcFile = await FindByFilePathAsync(normalizedSource, cancellationToken)
            .ConfigureAwait(false);
        if (srcFile == null)
        {
            throw new Exception("Source file cannot be found.");
        }

        // Find destination — source line 258: throws if exists and no overwrite
        var destFile = await FindByFilePathAsync(normalizedDest, cancellationToken)
            .ConfigureAwait(false);
        if (destFile != null && !overwrite)
        {
            throw new Exception("Destination file already exists and no overwrite specified.");
        }

        // Delete destination if overwrite — source lines 266-267
        if (destFile != null)
        {
            await DeleteByIdAsync(destFile.Id, cancellationToken).ConfigureAwait(false);
        }

        // Create new FileMetadata with new ID and destination filepath
        // Source line 270: Create(destinationFilepath, bytes, srcFile.CreatedOn, srcFile.CreatedBy)
        var newId = Guid.NewGuid();
        bool isDestTemp = normalizedDest.StartsWith(
            $"{FileMetadata.FolderSeparator}{FileMetadata.TmpFolderName}{FileMetadata.FolderSeparator}",
            StringComparison.OrdinalIgnoreCase);

        var newMetadata = new FileMetadata
        {
            Id = newId,
            FilePath = normalizedDest,
            ObjectKey = FileMetadata.GenerateObjectKey(newId, normalizedDest),
            ContentType = srcFile.ContentType,
            Size = srcFile.Size,
            CreatedBy = srcFile.CreatedBy,
            CreatedOn = srcFile.CreatedOn,
            LastModifiedBy = srcFile.LastModifiedBy,
            LastModificationDate = DateTime.UtcNow,
            IsTemp = isDestTemp,
            Ttl = isDestTemp
                ? DateTimeOffset.UtcNow.AddHours(_defaultTempFileTtlHours).ToUnixTimeSeconds()
                : null
        };

        // Persist new metadata record via CreateAsync
        var created = await CreateAsync(newMetadata, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Successfully copied file metadata. NewFileId={FileId}, Source={Source}, Destination={Destination}",
            created.Id,
            normalizedSource,
            normalizedDest);

        return created;
    }

    // ---------------------------------------------------------------------------
    // IFileMetadataRepository — Delete Operations
    // ---------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task DeleteAsync(
        string filepath,
        CancellationToken cancellationToken = default)
    {
        ValidateFilePath(filepath, nameof(filepath));
        var normalizedPath = FileMetadata.NormalizeFilePath(filepath);

        _logger.LogInformation(
            "Deleting file metadata by filepath. FilePath={FilePath}",
            normalizedPath);

        // Find file by filepath — source lines 385-388: return silently if null
        var file = await FindByFilePathAsync(normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        if (file == null)
        {
            _logger.LogInformation(
                "File metadata not found for deletion (no-op). FilePath={FilePath}",
                normalizedPath);
            return;
        }

        await DeleteByIdAsync(file.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Successfully deleted file metadata. FileId={FileId}, FilePath={FilePath}",
            file.Id,
            normalizedPath);
    }

    /// <inheritdoc />
    public async Task DeleteByIdAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Deleting file metadata by ID. FileId={FileId}",
            fileId);

        try
        {
            var request = new DeleteItemRequest
            {
                TableName = _tableName,
                Key = BuildPrimaryKey(fileId)
            };

            await _dynamoDbClient
                .DeleteItemAsync(request, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Successfully deleted file metadata by ID. FileId={FileId}",
                fileId);
        }
        catch (AmazonDynamoDBException ex)
        {
            _logger.LogError(
                ex,
                "DynamoDB error deleting file metadata. FileId={FileId}, ErrorCode={ErrorCode}",
                fileId,
                ex.ErrorCode);
            throw;
        }
    }

    // ---------------------------------------------------------------------------
    // IFileMetadataRepository — Temp File Operations
    // ---------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<FileMetadata> CreateTempFileMetadataAsync(
        string filename,
        string? extension,
        string contentType,
        long size,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new ArgumentException("filename cannot be null or empty", nameof(filename));
        }

        // Normalize extension — source lines 439-444
        string normalizedExtension = NormalizeTempFileExtension(extension);

        // Generate section GUID — source line 446
        string section = Guid.NewGuid()
            .ToString()
            .Replace("-", "")
            .ToLowerInvariant();

        // Build tmpFilePath — source line 447:
        // "/" + "tmp" + "/" + section + "/" + filename + extension
        string tmpFilePath = string.Concat(
            FOLDER_SEPARATOR,
            TMP_FOLDER_NAME,
            FOLDER_SEPARATOR,
            section,
            FOLDER_SEPARATOR,
            filename,
            normalizedExtension);

        _logger.LogInformation(
            "Creating temporary file metadata. FileName={FileName}, TmpFilePath={TmpPath}",
            filename,
            tmpFilePath);

        var fileId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var metadata = new FileMetadata
        {
            Id = fileId,
            FilePath = tmpFilePath,
            ObjectKey = FileMetadata.GenerateObjectKey(fileId, tmpFilePath),
            ContentType = contentType,
            Size = size,
            CreatedBy = null,       // Source line 448: null for createdBy
            CreatedOn = now,         // Source line 448: DateTime.UtcNow
            LastModifiedBy = null,
            LastModificationDate = now,
            IsTemp = true,
            // DynamoDB TTL replaces CleanupExpiredTempFiles() cron (source lines 455-469)
            Ttl = DateTimeOffset.UtcNow.AddHours(_defaultTempFileTtlHours).ToUnixTimeSeconds()
        };

        var created = await CreateAsync(metadata, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Successfully created temporary file metadata. FileId={FileId}, TmpPath={TmpPath}, TtlEpoch={Ttl}",
            created.Id,
            created.FilePath,
            created.Ttl);

        return created;
    }

    // ---------------------------------------------------------------------------
    // Private Helpers — Validation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Validates that a filepath is not null, empty, or whitespace.
    /// Replicates exact monolith validation from source lines 36-37, 121-122, 204-205, 377-378.
    /// </summary>
    /// <param name="filepath">The filepath to validate.</param>
    /// <param name="paramName">Parameter name for the error message context.</param>
    /// <exception cref="ArgumentException">Thrown when filepath is null, empty, or whitespace.</exception>
    private static void ValidateFilePath(string filepath, string paramName)
    {
        if (string.IsNullOrWhiteSpace(filepath))
        {
            throw new ArgumentException("filepath cannot be null or empty", paramName);
        }
    }

    /// <summary>
    /// Normalizes a temp file extension to lowercase with a leading dot.
    /// Replicates exact monolith behavior from source lines 439-444.
    /// </summary>
    /// <param name="extension">The file extension to normalize.</param>
    /// <returns>The normalized extension, or empty string if null/whitespace.</returns>
    private static string NormalizeTempFileExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var normalized = extension.Trim().ToLowerInvariant();

        // Ensure leading dot — source line 444
        if (!normalized.StartsWith('.'))
        {
            normalized = "." + normalized;
        }

        return normalized;
    }

    // ---------------------------------------------------------------------------
    // Private Helpers — DynamoDB Key Construction
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds the DynamoDB composite primary key for a file metadata item.
    /// </summary>
    /// <param name="fileId">The unique file identifier.</param>
    /// <returns>Dictionary representing the PK/SK pair.</returns>
    private static Dictionary<string, AttributeValue> BuildPrimaryKey(Guid fileId)
    {
        return new Dictionary<string, AttributeValue>
        {
            { PkAttr, new AttributeValue { S = $"{PkPrefix}{fileId}" } },
            { SkAttr, new AttributeValue { S = SkValue } }
        };
    }

    // ---------------------------------------------------------------------------
    // Private Helpers — DynamoDB Item Mapping
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Maps a <see cref="FileMetadata"/> domain model to a DynamoDB item attribute map.
    /// Handles nullable Guid fields (CreatedBy, LastModifiedBy) and optional TTL attribute.
    /// </summary>
    /// <param name="metadata">The domain model to serialize.</param>
    /// <returns>DynamoDB item attribute dictionary.</returns>
    private static Dictionary<string, AttributeValue> ToAttributeMap(FileMetadata metadata)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { PkAttr, new AttributeValue { S = $"{PkPrefix}{metadata.Id}" } },
            { SkAttr, new AttributeValue { S = SkValue } },
            { IdAttr, new AttributeValue { S = metadata.Id.ToString() } },
            { FilepathAttr, new AttributeValue { S = metadata.FilePath } },
            { ObjectKeyAttr, new AttributeValue { S = metadata.ObjectKey } },
            { ContentTypeAttr, new AttributeValue { S = metadata.ContentType } },
            { SizeAttr, new AttributeValue { N = metadata.Size.ToString() } },
            { CreatedOnAttr, new AttributeValue { S = metadata.CreatedOn.ToString("O") } },
            { LastModificationDateAttr, new AttributeValue { S = metadata.LastModificationDate.ToString("O") } },
            { IsTempAttr, new AttributeValue { BOOL = metadata.IsTemp } }
        };

        // Nullable CreatedBy — source DbFile.cs lines 27-29, 161 (nullable Guid? from DataRow with DBNull check)
        if (metadata.CreatedBy.HasValue)
        {
            item[CreatedByAttr] = new AttributeValue { S = metadata.CreatedBy.Value.ToString() };
        }
        else
        {
            item[CreatedByAttr] = new AttributeValue { NULL = true };
        }

        // Nullable LastModifiedBy — source DbFile.cs lines 31-33, 162
        if (metadata.LastModifiedBy.HasValue)
        {
            item[LastModifiedByAttr] = new AttributeValue { S = metadata.LastModifiedBy.Value.ToString() };
        }
        else
        {
            item[LastModifiedByAttr] = new AttributeValue { NULL = true };
        }

        // TTL attribute — only meaningful for temp files (DynamoDB auto-deletes expired items)
        if (metadata.Ttl.HasValue)
        {
            item[TtlAttr] = new AttributeValue { N = metadata.Ttl.Value.ToString() };
        }

        return item;
    }

    /// <summary>
    /// Maps a DynamoDB item attribute dictionary to a <see cref="FileMetadata"/> domain model.
    /// Uses safe extraction with TryGetValue to handle missing attributes gracefully.
    /// </summary>
    /// <param name="item">The DynamoDB item to deserialize.</param>
    /// <returns>The deserialized domain model.</returns>
    private static FileMetadata FromAttributeMap(Dictionary<string, AttributeValue> item)
    {
        var metadata = new FileMetadata
        {
            Id = Guid.Parse(GetStringValue(item, IdAttr)),
            FilePath = GetStringValue(item, FilepathAttr),
            ObjectKey = GetStringValue(item, ObjectKeyAttr, string.Empty),
            ContentType = GetStringValue(item, ContentTypeAttr, "application/octet-stream"),
            Size = GetNumericLongValue(item, SizeAttr, 0),
            CreatedOn = GetDateTimeValue(item, CreatedOnAttr, DateTime.UtcNow),
            LastModificationDate = GetDateTimeValue(item, LastModificationDateAttr, DateTime.UtcNow),
            CreatedBy = GetNullableGuidValue(item, CreatedByAttr),
            LastModifiedBy = GetNullableGuidValue(item, LastModifiedByAttr),
            IsTemp = GetBoolValue(item, IsTempAttr, false),
            Ttl = GetNullableLongValue(item, TtlAttr)
        };

        return metadata;
    }

    // ---------------------------------------------------------------------------
    // Private Helpers — Safe Attribute Value Extraction
    // ---------------------------------------------------------------------------

    /// <summary>Extracts a string attribute value, throwing if missing and no default provided.</summary>
    private static string GetStringValue(
        Dictionary<string, AttributeValue> item,
        string attributeName,
        string? defaultValue = null)
    {
        if (item.TryGetValue(attributeName, out var attr) && attr.S != null)
        {
            return attr.S;
        }

        if (defaultValue != null)
        {
            return defaultValue;
        }

        throw new InvalidOperationException(
            $"Required DynamoDB attribute '{attributeName}' is missing or not a string.");
    }

    /// <summary>Extracts a boolean attribute value with a default fallback.</summary>
    private static bool GetBoolValue(
        Dictionary<string, AttributeValue> item,
        string attributeName,
        bool defaultValue)
    {
        if (item.TryGetValue(attributeName, out var attr) && attr.IsBOOLSet)
        {
            return attr.BOOL;
        }

        return defaultValue;
    }

    /// <summary>Extracts a long numeric attribute value with a default fallback.</summary>
    private static long GetNumericLongValue(
        Dictionary<string, AttributeValue> item,
        string attributeName,
        long defaultValue)
    {
        if (item.TryGetValue(attributeName, out var attr) && attr.N != null)
        {
            if (long.TryParse(attr.N, out var result))
            {
                return result;
            }
        }

        return defaultValue;
    }

    /// <summary>Extracts an optional long numeric attribute value (nullable).</summary>
    private static long? GetNullableLongValue(
        Dictionary<string, AttributeValue> item,
        string attributeName)
    {
        if (item.TryGetValue(attributeName, out var attr) && attr.N != null)
        {
            if (long.TryParse(attr.N, out var result))
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>Extracts a DateTime from an ISO 8601 string attribute with a default fallback.</summary>
    private static DateTime GetDateTimeValue(
        Dictionary<string, AttributeValue> item,
        string attributeName,
        DateTime defaultValue)
    {
        if (item.TryGetValue(attributeName, out var attr) && attr.S != null)
        {
            if (DateTime.TryParse(attr.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
            {
                return result;
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Extracts an optional Guid from a string attribute (nullable).
    /// Handles DynamoDB NULL type and missing attributes gracefully,
    /// matching source DbFile.cs lines 27-33 DBNull handling.
    /// </summary>
    private static Guid? GetNullableGuidValue(
        Dictionary<string, AttributeValue> item,
        string attributeName)
    {
        if (item.TryGetValue(attributeName, out var attr))
        {
            // Handle explicit NULL type
            if (attr.NULL)
            {
                return null;
            }

            if (attr.S != null && Guid.TryParse(attr.S, out var result))
            {
                return result;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------
    // Private Helpers — Pagination Token Serialization
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Serializes a DynamoDB <c>LastEvaluatedKey</c> to a Base64-encoded JSON string
    /// for use as an opaque cursor token. Replaces SQL OFFSET/LIMIT pagination
    /// (source lines 69-80) with DynamoDB-native cursor-based pagination.
    /// </summary>
    /// <param name="lastEvaluatedKey">The DynamoDB pagination key dictionary.</param>
    /// <returns>Base64-encoded JSON string, or <c>null</c> if no more pages.</returns>
    private static string? SerializeLastEvaluatedKey(
        Dictionary<string, AttributeValue>? lastEvaluatedKey)
    {
        if (lastEvaluatedKey == null || lastEvaluatedKey.Count == 0)
        {
            return null;
        }

        // Convert AttributeValue dictionary to a simple serializable dictionary
        var serializableKey = new Dictionary<string, Dictionary<string, string>>();
        foreach (var kvp in lastEvaluatedKey)
        {
            var attrDict = new Dictionary<string, string>();
            if (kvp.Value.S != null)
            {
                attrDict["S"] = kvp.Value.S;
            }
            else if (kvp.Value.N != null)
            {
                attrDict["N"] = kvp.Value.N;
            }
            else if (kvp.Value.IsBOOLSet)
            {
                attrDict["BOOL"] = kvp.Value.BOOL.ToString();
            }
            serializableKey[kvp.Key] = attrDict;
        }

        var json = JsonSerializer.Serialize(
            serializableKey,
            PaginationKeyJsonContext.Default.DictionaryStringDictionaryStringString);
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Deserializes a Base64-encoded JSON pagination cursor back to a DynamoDB
    /// <c>ExclusiveStartKey</c> dictionary.
    /// </summary>
    /// <param name="exclusiveStartKey">The Base64-encoded JSON cursor token.</param>
    /// <returns>The DynamoDB key dictionary, or <c>null</c> if input is empty.</returns>
    private static Dictionary<string, AttributeValue>? DeserializeExclusiveStartKey(
        string? exclusiveStartKey)
    {
        if (string.IsNullOrWhiteSpace(exclusiveStartKey))
        {
            return null;
        }

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(exclusiveStartKey));

            var serializableKey = JsonSerializer.Deserialize(
                json,
                PaginationKeyJsonContext.Default.DictionaryStringDictionaryStringString);
            if (serializableKey == null)
            {
                return null;
            }

            var key = new Dictionary<string, AttributeValue>();
            foreach (var kvp in serializableKey)
            {
                var attrValue = new AttributeValue();
                if (kvp.Value.TryGetValue("S", out var sVal))
                {
                    attrValue.S = sVal;
                }
                else if (kvp.Value.TryGetValue("N", out var nVal))
                {
                    attrValue.N = nVal;
                }
                else if (kvp.Value.TryGetValue("BOOL", out var bVal))
                {
                    attrValue.BOOL = bool.Parse(bVal);
                }
                key[kvp.Key] = attrValue;
            }

            // Validate deserialized key contains expected primary key attributes
            if (!key.ContainsKey(PkAttr) || !key.ContainsKey(SkAttr))
            {
                return null;
            }

            return key;
        }
        catch (Exception)
        {
            // Invalid cursor token — return null to start from the beginning
            return null;
        }
    }
}

/// <summary>
/// AOT-compatible source-generated JSON serializer context for DynamoDB pagination token
/// serialization/deserialization. Required for .NET 9 Native AOT Lambda targets to avoid
/// IL2026/IL3050 warnings from <see cref="JsonSerializer"/>.
/// </summary>
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal partial class PaginationKeyJsonContext : JsonSerializerContext
{
}
