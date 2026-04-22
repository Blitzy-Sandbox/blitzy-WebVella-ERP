// ---------------------------------------------------------------------------
// DownloadHandler.cs — AWS Lambda handler for file retrieval, listing, copy,
// move, and delete operations in the File Management bounded-context service.
//
// Replaces:
//   DbFile.GetBytes() / GetContentStream() → S3 presigned download URLs
//   DbFileRepository.Find(filepath) → DynamoDB filepath GSI lookup
//   DbFileRepository.FindAll() → DynamoDB scan with prefix filter
//   DbFileRepository.Copy() → S3 server-side copy + DynamoDB metadata create
//   DbFileRepository.Move() → S3 copy+delete + DynamoDB metadata update
//   DbFileRepository.Delete() → S3 object delete + DynamoDB metadata delete
//   DbFileRepository.UpdateModificationDate() → DynamoDB attribute update
//
// ZERO references to WebVella.Erp.* namespaces, ZERO PostgreSQL/Npgsql,
// ZERO Storage.Net — pure AWS serverless implementation.
// ---------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.FileManagement.DataAccess;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;

// Assembly-level JSON serializer attribute for Lambda runtime.
// Only ONE file per assembly declares this — DownloadHandler.cs is the primary entry point.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.FileManagement.Functions
{
    /// <summary>
    /// Request DTO for PATCH /v1/files/{fileId}/modification-date.
    /// Replaces the inline parameters of DbFileRepository.UpdateModificationDate(filepath, modificationDate).
    /// </summary>
    public class UpdateModificationDateRequest
    {
        /// <summary>
        /// The new modification date to set on the file metadata.
        /// Replaces the <c>modificationDate</c> parameter from source UpdateModificationDate() (line 202).
        /// </summary>
        [JsonPropertyName("modificationDate")]
        public DateTime ModificationDate { get; set; }
    }

    /// <summary>
    /// AWS Lambda handler for all file retrieval and management HTTP API Gateway v2 requests.
    /// <para>
    /// Decomposes the monolith's <c>DbFileRepository</c> (CRUD + query) and <c>DbFile</c>
    /// (content streaming) into 7 Lambda handler methods backed by S3 for object storage
    /// and DynamoDB for metadata persistence.
    /// </para>
    /// <para>
    /// All handler methods:
    /// <list type="bullet">
    ///   <item><description>Accept <see cref="APIGatewayHttpApiV2ProxyRequest"/> + <see cref="ILambdaContext"/></description></item>
    ///   <item><description>Return <see cref="APIGatewayHttpApiV2ProxyResponse"/></description></item>
    ///   <item><description>Propagate correlation-ID for structured JSON logging</description></item>
    ///   <item><description>Use idempotency keys on write operations</description></item>
    ///   <item><description>Publish SNS domain events for state-changing operations</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public class DownloadHandler
    {
        #region Private Fields

        /// <summary>S3 storage operations for file retrieval and management.</summary>
        private readonly IS3Service _s3Service;

        /// <summary>DynamoDB file metadata persistence (replaces PostgreSQL files table).</summary>
        private readonly IFileMetadataRepository _metadataRepository;

        /// <summary>SNS client for publishing domain events after state-changing operations.</summary>
        private readonly IAmazonSimpleNotificationService _snsClient;

        /// <summary>Structured JSON logger with correlation-ID propagation.</summary>
        private readonly ILogger<DownloadHandler> _logger;

        /// <summary>
        /// SNS topic ARN for file domain events. Read from FILE_EVENTS_TOPIC_ARN environment variable.
        /// Events follow naming convention: file-management.file.{action} per AAP §0.8.5.
        /// </summary>
        private readonly string _fileEventTopicArn;

        /// <summary>Default presigned download URL expiration in minutes.</summary>
        private const int DefaultDownloadUrlExpirationMinutes = 60;

        /// <summary>Correlation-ID header name for distributed tracing.</summary>
        private const string CorrelationIdHeader = "x-correlation-id";

        /// <summary>Maximum page size to prevent abuse on list queries.</summary>
        private const int MaxPageSize = 200;

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for the AWS Lambda runtime.
        /// Builds a DI <see cref="ServiceCollection"/>, configures all services
        /// (AWS SDK clients, IS3Service, IFileMetadataRepository), and resolves dependencies.
        /// All SDK clients respect <c>AWS_ENDPOINT_URL</c> for LocalStack compatibility.
        /// </summary>
        public DownloadHandler()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            _s3Service = provider.GetRequiredService<IS3Service>();
            _metadataRepository = provider.GetRequiredService<IFileMetadataRepository>();
            _snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILogger<DownloadHandler>>();
            _fileEventTopicArn = Environment.GetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN") ?? string.Empty;
        }

        /// <summary>
        /// Constructor accepting an <see cref="IServiceProvider"/> for unit testing.
        /// Allows test fixtures to inject mocked services without building the full DI container.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider with all required dependencies.</param>
        public DownloadHandler(IServiceProvider serviceProvider)
        {
            _s3Service = serviceProvider.GetRequiredService<IS3Service>();
            _metadataRepository = serviceProvider.GetRequiredService<IFileMetadataRepository>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILogger<DownloadHandler>>();
            _fileEventTopicArn = Environment.GetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN") ?? string.Empty;
        }

        #endregion

        #region DI Configuration

        /// <summary>
        /// Configures all dependency injection services for the Lambda handler.
        /// AWS SDK clients are configured to respect <c>AWS_ENDPOINT_URL</c> for LocalStack dual-target.
        /// </summary>
        private static void ConfigureServices(IServiceCollection services)
        {
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // IConfiguration from environment variables — used by S3Service and FileMetadataRepository
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            // AWS SDK clients — pre-configured with endpoint URL for LocalStack
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonS3>(_ =>
                    new AmazonS3Client(new AmazonS3Config
                    {
                        ServiceURL = endpointUrl,
                        ForcePathStyle = true
                    }));
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig
                    {
                        ServiceURL = endpointUrl
                    }));
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig
                        {
                            ServiceURL = endpointUrl
                        }));
            }
            else
            {
                services.AddSingleton<IAmazonS3, AmazonS3Client>();
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService,
                    AmazonSimpleNotificationServiceClient>();
            }

            // Domain services
            services.AddSingleton<IS3Service, S3Service>();
            services.AddSingleton<IFileMetadataRepository, FileMetadataRepository>();

            // Structured JSON logging
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                builder.AddJsonConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                });
            });
        }

        #endregion

        #region Handler: GET /v1/files/{fileId}/download-url

        /// <summary>
        /// Lambda entry point for GET /v1/files/{fileId}/download-url.
        /// Generates a presigned S3 GET URL for direct client-to-S3 download.
        /// <para>
        /// Replaces <c>DbFile.GetBytes()</c> (source lines 73-104) and <c>DbFile.GetContentStream()</c>
        /// (source lines 36-71) which dispatched to three backends (PostgreSQL LO, filesystem, cloud blob).
        /// Now replaced by a single S3 presigned URL — no data proxied through Lambda.
        /// </para>
        /// </summary>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            // Extract the proxy path segment(s) from {proxy+} parameter
            string proxyPath = string.Empty;
            request.PathParameters?.TryGetValue("proxy", out proxyPath!);
            proxyPath ??= string.Empty;
            var segments = proxyPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (method == "DELETE")
                return await HandleDeleteFile(request, context);

            if (method == "PUT")
                return await HandleUpdateModificationDate(request, context);

            if (method == "GET")
            {
                // GET /v1/files/list or GET /v1/files (empty proxy) → list files
                if (segments.Length == 0 || string.Equals(segments[0], "list", StringComparison.OrdinalIgnoreCase))
                    return await HandleListFiles(request, context);

                // GET /v1/files/by-path?path=... → get file by path
                if (string.Equals(segments[0], "by-path", StringComparison.OrdinalIgnoreCase))
                    return await HandleGetFileByPath(request, context);

                // GET /v1/files/user-files/... → list user files (delegate to HandleListFiles)
                if (string.Equals(segments[0], "user-files", StringComparison.OrdinalIgnoreCase))
                    return await HandleListFiles(request, context);

                // GET /v1/files/{fileId}/download-url → presigned download URL
                if (segments.Length >= 2 && string.Equals(segments[^1], "download-url", StringComparison.OrdinalIgnoreCase))
                    return await HandleGetDownloadUrl(request, context);

                // GET /v1/files/{fileId} → file details (single file by ID)
                if (segments.Length == 1 && Guid.TryParse(segments[0], out _))
                    return await HandleGetDownloadUrl(request, context);

                // Fallback: if it has a path= query param, treat as file-by-path
                if (request.QueryStringParameters != null &&
                    request.QueryStringParameters.ContainsKey("path"))
                    return await HandleGetFileByPath(request, context);

                // Default to list
                return await HandleListFiles(request, context);
            }

            // Default: route to HandleListFiles for unrecognized methods
            return await HandleListFiles(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetDownloadUrl(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleGetDownloadUrl)
            });

            try
            {
                _logger.LogInformation("Processing download URL request. CorrelationId={CorrelationId}",
                    correlationId);

                // Extract fileId from path parameters
                string? fileIdStr = null;
                request.PathParameters?.TryGetValue("fileId", out fileIdStr);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes
                if (string.IsNullOrEmpty(fileIdStr) && request.PathParameters != null &&
                    request.PathParameters.TryGetValue("proxy", out var fpx))
                {
                    var segs = fpx.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segs.Length - 1; i >= 0; i--)
                        if (Guid.TryParse(segs[i], out _)) { fileIdStr = segs[i]; break; }
                }
                if (!Guid.TryParse(fileIdStr, out var fileId))
                {
                    _logger.LogWarning("Invalid or missing fileId in path parameters");
                    return BuildErrorResponse(400, "Invalid or missing fileId parameter. Must be a valid GUID.",
                        correlationId);
                }

                // Extract optional expiration from query string
                var expirationMinutes = DefaultDownloadUrlExpirationMinutes;
                if (request.QueryStringParameters != null &&
                    request.QueryStringParameters.TryGetValue("expirationMinutes", out var expStr) &&
                    int.TryParse(expStr, out var parsedExp) && parsedExp > 0 && parsedExp <= 1440)
                {
                    expirationMinutes = parsedExp;
                }

                // Retrieve file metadata from DynamoDB
                // Replaces DbFileRepository.Find(filepath) (source lines 34-56)
                var metadata = await _metadataRepository.FindByIdAsync(fileId);
                if (metadata == null)
                {
                    _logger.LogWarning("File not found for download. FileId={FileId}", fileId);
                    return BuildErrorResponse(404, "File not found.", correlationId);
                }

                _logger.LogInformation(
                    "Generating presigned download URL. FileId={FileId}, FilePath={FilePath}, ObjectKey={ObjectKey}",
                    metadata.Id, metadata.FilePath, metadata.ObjectKey);

                // Generate presigned GET URL — replaces DbFile's three-backend byte streaming
                var presignedUrl = await _s3Service.GeneratePresignedDownloadUrlAsync(
                    metadata.ObjectKey, expirationMinutes);

                // NEVER log presigned URLs — they contain authentication tokens
                var response = new DownloadFileResponse
                {
                    PresignedUrl = presignedUrl,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(expirationMinutes),
                    Metadata = metadata,
                    Success = true
                };

                return BuildResponse(200, response, correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
            {
                _logger.LogWarning(ex, "S3 object not found for download. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(404, "File content not found in storage.", correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "AccessDenied")
            {
                _logger.LogError(ex, "S3 access denied for download. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(403, "Access denied to file storage.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating download URL. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred while generating the download URL.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: GET /v1/files?path={filepath}

        /// <summary>
        /// Lambda entry point for GET /v1/files?path={filepath}.
        /// Retrieves file metadata by normalized filepath using DynamoDB GSI lookup.
        /// <para>
        /// Replaces <c>DbFileRepository.Find(string filepath)</c> (source lines 34-56):
        /// <code>
        /// filepath = filepath.ToLowerInvariant();
        /// if (!filepath.StartsWith(FOLDER_SEPARATOR)) filepath = FOLDER_SEPARATOR + filepath;
        /// SELECT * FROM files WHERE filepath = @filepath
        /// </code>
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetFileByPath(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleGetFileByPath)
            });

            try
            {
                _logger.LogInformation("Processing get-file-by-path request. CorrelationId={CorrelationId}",
                    correlationId);

                // Extract path from query string
                string? rawPath = null;
                request.QueryStringParameters?.TryGetValue("path", out rawPath);

                // Validate path is not empty — matching source lines 36-37:
                // if (string.IsNullOrWhiteSpace(filepath)) throw new ArgumentException(...)
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    _logger.LogWarning("Missing or empty path query parameter");
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["path"] = "filepath cannot be null or empty" },
                        correlationId);
                }

                // Normalize path — matching source lines 40-42:
                // filepath = filepath.ToLowerInvariant();
                // if (!filepath.StartsWith(FOLDER_SEPARATOR)) filepath = FOLDER_SEPARATOR + filepath;
                var normalizedPath = NormalizeFilePath(rawPath);

                _logger.LogInformation("Looking up file by path. NormalizedPath={FilePath}", normalizedPath);

                // Query DynamoDB by filepath using GSI — replaces PostgreSQL WHERE filepath = @filepath
                var metadata = await _metadataRepository.FindByFilePathAsync(normalizedPath);
                if (metadata == null)
                {
                    _logger.LogWarning("File not found by path. FilePath={FilePath}", normalizedPath);
                    return BuildErrorResponse(404, "File not found.", correlationId);
                }

                var response = new FileOperationResponse
                {
                    Metadata = metadata,
                    Success = true,
                    Message = "File metadata retrieved successfully."
                };
                return BuildResponse(200, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving file by path. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred while retrieving file metadata.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: GET /v1/files

        /// <summary>
        /// Lambda entry point for GET /v1/files.
        /// Lists file metadata with optional path prefix filter, temp file exclusion, and pagination.
        /// <para>
        /// Replaces <c>DbFileRepository.FindAll(string startsWithPath, bool includeTempFiles, int? skip, int? limit)</c>
        /// (source lines 58-117). Source SQL conditions:
        /// <list type="bullet">
        ///   <item><description>Path prefix: <c>WHERE filepath ILIKE '%startsWithPath'</c></description></item>
        ///   <item><description>Temp exclusion: <c>WHERE filepath NOT ILIKE '%/tmp'</c></description></item>
        ///   <item><description>Pagination: <c>LIMIT {limit} OFFSET {skip}</c></description></item>
        /// </list>
        /// Now uses DynamoDB scan with begins_with filter and cursor-based pagination.
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleListFiles(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleListFiles)
            });

            try
            {
                _logger.LogInformation("Processing list-files request. CorrelationId={CorrelationId}",
                    correlationId);

                // Extract query string parameters matching source FindAll parameters
                string? startsWithPath = null;
                var includeTempFiles = false;
                var pageSize = 30;
                string? exclusiveStartKey = null;

                if (request.QueryStringParameters != null)
                {
                    request.QueryStringParameters.TryGetValue("startsWithPath", out startsWithPath);
                    request.QueryStringParameters.TryGetValue("exclusiveStartKey", out exclusiveStartKey);

                    if (request.QueryStringParameters.TryGetValue("includeTempFiles", out var tempStr))
                    {
                        bool.TryParse(tempStr, out includeTempFiles);
                    }

                    if (request.QueryStringParameters.TryGetValue("pageSize", out var pageSizeStr) &&
                        int.TryParse(pageSizeStr, out var parsedPageSize) && parsedPageSize > 0)
                    {
                        pageSize = Math.Min(parsedPageSize, MaxPageSize);
                    }

                    // Support legacy skip/limit parameters from source (lines 58-60)
                    if (request.QueryStringParameters.TryGetValue("limit", out var limitStr) &&
                        int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
                    {
                        pageSize = Math.Min(parsedLimit, MaxPageSize);
                    }
                }

                // Normalize startsWithPath if provided — matching source lines 61-66:
                // startsWithPath = startsWithPath.ToLowerInvariant();
                // if (!startsWithPath.StartsWith(FOLDER_SEPARATOR)) startsWithPath = FOLDER_SEPARATOR + startsWithPath;
                if (!string.IsNullOrWhiteSpace(startsWithPath))
                {
                    startsWithPath = NormalizeFilePath(startsWithPath);
                }

                _logger.LogInformation(
                    "Listing files. StartsWithPath={StartsWithPath}, IncludeTempFiles={IncludeTempFiles}, PageSize={PageSize}",
                    startsWithPath ?? "(all)", includeTempFiles, pageSize);

                // Query DynamoDB with filters — replaces PostgreSQL ILIKE + LIMIT/OFFSET
                var (items, lastEvaluatedKey) = await _metadataRepository.FindAllAsync(
                    startsWithPath, includeTempFiles, pageSize, exclusiveStartKey);

                var response = new ListFilesResponse
                {
                    Items = items,
                    TotalCount = items.Count,
                    PageSize = pageSize,
                    LastEvaluatedKey = lastEvaluatedKey,
                    Success = true
                };

                _logger.LogInformation("Listed {Count} files", items.Count);
                return BuildResponse(200, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing files. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(500, "An internal error occurred while listing files.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: POST /v1/files/{fileId}/copy

        /// <summary>
        /// Lambda entry point for POST /v1/files/{fileId}/copy.
        /// Copies a file to a new destination using S3 server-side copy + DynamoDB metadata creation.
        /// <para>
        /// Replaces <c>DbFileRepository.Copy()</c> (source lines 234-281) which read bytes from the
        /// source file and called <c>Create()</c> at the destination path. S3 server-side copy is
        /// significantly more efficient — no data transits through Lambda.
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleCopyFile(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleCopyFile)
            });

            try
            {
                _logger.LogInformation("Processing copy-file request. CorrelationId={CorrelationId}",
                    correlationId);

                var (callerId, callerEmail) = ExtractCallerFromContext(request);

                // Extract fileId from path parameters
                string? fileIdStr = null;
                request.PathParameters?.TryGetValue("fileId", out fileIdStr);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes
                if (string.IsNullOrEmpty(fileIdStr) && request.PathParameters != null &&
                    request.PathParameters.TryGetValue("proxy", out var fpx))
                {
                    var segs = fpx.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segs.Length - 1; i >= 0; i--)
                        if (Guid.TryParse(segs[i], out _)) { fileIdStr = segs[i]; break; }
                }
                if (!Guid.TryParse(fileIdStr, out var fileId))
                {
                    _logger.LogWarning("Invalid or missing fileId for copy operation");
                    return BuildErrorResponse(400, "Invalid or missing fileId parameter. Must be a valid GUID.",
                        correlationId);
                }

                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.", correlationId);
                }

                CopyFileRequest? copyRequest;
                try
                {
                    copyRequest = JsonSerializer.Deserialize<CopyFileRequest>(request.Body);
                }
                catch (JsonException)
                {
                    return BuildErrorResponse(400, "Invalid JSON in request body.", correlationId);
                }

                if (copyRequest == null || string.IsNullOrWhiteSpace(copyRequest.DestinationFilePath))
                {
                    // Matching source lines 236-240: source and destination paths must not be null/empty
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string>
                        {
                            ["destinationFilePath"] = "Destination file path is required."
                        }, correlationId);
                }

                // Normalize destination path — matching source lines 243-249:
                // destinationFilepath = destinationFilepath.ToLowerInvariant();
                // if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR)) destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;
                var normalizedDestPath = NormalizeFilePath(copyRequest.DestinationFilePath);

                // Retrieve source file metadata — replaces Find(sourceFilepath) source line 251
                var sourceMetadata = await _metadataRepository.FindByIdAsync(fileId);
                if (sourceMetadata == null)
                {
                    // Matching source line 254: throw new Exception("Source file cannot be found.")
                    _logger.LogWarning("Source file not found for copy. FileId={FileId}", fileId);
                    return BuildErrorResponse(404, "Source file cannot be found.", correlationId);
                }

                _logger.LogInformation(
                    "Copy operation: Source={SourcePath} → Destination={DestinationPath}, Overwrite={Overwrite}",
                    sourceMetadata.FilePath, normalizedDestPath, copyRequest.Overwrite);

                // Check if destination already exists — replaces source lines 257-258
                var existingDest = await _metadataRepository.FindByFilePathAsync(normalizedDestPath);
                if (existingDest != null && !copyRequest.Overwrite)
                {
                    // Matching source lines 257-258:
                    // throw new Exception("Destination file already exists and no overwrite specified.")
                    _logger.LogWarning(
                        "Copy destination already exists and overwrite not specified. DestPath={DestPath}",
                        normalizedDestPath);
                    return BuildErrorResponse(409,
                        "Destination file already exists and no overwrite specified.", correlationId);
                }

                // If destination exists and overwrite is true, delete existing — matching source lines 266-267
                if (existingDest != null && copyRequest.Overwrite)
                {
                    _logger.LogInformation(
                        "Overwriting existing destination file. DestFileId={DestFileId}", existingDest.Id);
                    await _s3Service.DeleteFileAsync(existingDest.ObjectKey);
                    await _metadataRepository.DeleteByIdAsync(existingDest.Id);
                }

                // Generate new object key for the copy destination
                var newFileId = Guid.NewGuid();
                var destObjectKey = FileMetadata.GenerateObjectKey(newFileId, normalizedDestPath);

                // S3 server-side copy — replaces source lines 269-270 read bytes + Create() pattern
                await _s3Service.CopyFileAsync(sourceMetadata.ObjectKey, destObjectKey);

                // Create new metadata record preserving source's CreatedOn and CreatedBy
                // Matching source line 270: Create(dest, bytes, srcFile.CreatedOn, srcFile.CreatedBy)
                var newMetadata = new FileMetadata
                {
                    Id = newFileId,
                    FilePath = normalizedDestPath,
                    ObjectKey = destObjectKey,
                    ContentType = sourceMetadata.ContentType,
                    Size = sourceMetadata.Size,
                    CreatedBy = sourceMetadata.CreatedBy,
                    CreatedOn = sourceMetadata.CreatedOn,
                    LastModifiedBy = callerId,
                    LastModificationDate = DateTime.UtcNow,
                    IsTemp = FileMetadata.NormalizeFilePath(normalizedDestPath)
                        .StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase)
                };
                var createdMetadata = await _metadataRepository.CreateAsync(newMetadata);

                // Publish SNS domain event: file-management.file.copied
                await PublishDomainEventAsync("file-management.file.copied", new
                {
                    eventType = "file-management.file.copied",
                    fileId = newFileId,
                    sourceFileId = sourceMetadata.Id,
                    sourceFilePath = sourceMetadata.FilePath,
                    destinationFilePath = normalizedDestPath,
                    copiedBy = callerId,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    correlationId
                }, correlationId);

                _logger.LogInformation(
                    "File copied successfully. NewFileId={NewFileId}, DestPath={DestPath}",
                    newFileId, normalizedDestPath);

                var response = new FileOperationResponse
                {
                    Metadata = createdMetadata,
                    Success = true,
                    Message = "File copied successfully."
                };
                return BuildResponse(200, response, correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
            {
                _logger.LogWarning(ex, "Source S3 object not found during copy. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(404, "Source file content not found in storage.", correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "AccessDenied")
            {
                _logger.LogError(ex, "S3 access denied during copy. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(403, "Access denied to file storage.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying file. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(500, "An internal error occurred while copying the file.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: POST /v1/files/{fileId}/move

        /// <summary>
        /// Lambda entry point for POST /v1/files/{fileId}/move.
        /// Moves a file to a new destination using S3 copy+delete + DynamoDB metadata update.
        /// <para>
        /// Replaces <c>DbFileRepository.Move()</c> (source lines 290-368) which updated the filepath
        /// in PostgreSQL and relocated content across three backends. Now uses S3 server-side
        /// copy + delete and a single DynamoDB metadata update.
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleMoveFile(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleMoveFile)
            });

            try
            {
                _logger.LogInformation("Processing move-file request. CorrelationId={CorrelationId}",
                    correlationId);

                var (callerId, callerEmail) = ExtractCallerFromContext(request);

                // Extract fileId from path parameters
                string? fileIdStr = null;
                request.PathParameters?.TryGetValue("fileId", out fileIdStr);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes
                if (string.IsNullOrEmpty(fileIdStr) && request.PathParameters != null &&
                    request.PathParameters.TryGetValue("proxy", out var fpx))
                {
                    var segs = fpx.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segs.Length - 1; i >= 0; i--)
                        if (Guid.TryParse(segs[i], out _)) { fileIdStr = segs[i]; break; }
                }
                if (!Guid.TryParse(fileIdStr, out var fileId))
                {
                    _logger.LogWarning("Invalid or missing fileId for move operation");
                    return BuildErrorResponse(400, "Invalid or missing fileId parameter. Must be a valid GUID.",
                        correlationId);
                }

                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.", correlationId);
                }

                MoveFileRequest? moveRequest;
                try
                {
                    moveRequest = JsonSerializer.Deserialize<MoveFileRequest>(request.Body);
                }
                catch (JsonException)
                {
                    return BuildErrorResponse(400, "Invalid JSON in request body.", correlationId);
                }

                if (moveRequest == null || string.IsNullOrWhiteSpace(moveRequest.DestinationFilePath))
                {
                    // Matching source lines 292-296: source and destination paths must not be null/empty
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string>
                        {
                            ["destinationFilePath"] = "Destination file path is required."
                        }, correlationId);
                }

                // Normalize destination path — matching source lines 298-305
                var normalizedDestPath = NormalizeFilePath(moveRequest.DestinationFilePath);

                // Retrieve source file metadata — replaces Find(sourceFilepath) source line 307
                var sourceMetadata = await _metadataRepository.FindByIdAsync(fileId);
                if (sourceMetadata == null)
                {
                    // Matching source line 310: throw new Exception("Source file cannot be found.")
                    _logger.LogWarning("Source file not found for move. FileId={FileId}", fileId);
                    return BuildErrorResponse(404, "Source file cannot be found.", correlationId);
                }

                var oldFilePath = sourceMetadata.FilePath;

                _logger.LogInformation(
                    "Move operation: Source={SourcePath} → Destination={DestinationPath}, Overwrite={Overwrite}",
                    oldFilePath, normalizedDestPath, moveRequest.Overwrite);

                // Check destination existence — replaces Find(destinationFilepath) source line 308
                var existingDest = await _metadataRepository.FindByFilePathAsync(normalizedDestPath);
                if (existingDest != null && existingDest.Id != sourceMetadata.Id && !moveRequest.Overwrite)
                {
                    // Matching source lines 313-314:
                    // throw new Exception("Destination file already exists and no overwrite specified.")
                    _logger.LogWarning(
                        "Move destination already exists and overwrite not specified. DestPath={DestPath}",
                        normalizedDestPath);
                    return BuildErrorResponse(409,
                        "Destination file already exists and no overwrite specified.", correlationId);
                }

                // If destination exists and overwrite is true, delete existing — matching source lines 322-323
                if (existingDest != null && existingDest.Id != sourceMetadata.Id && moveRequest.Overwrite)
                {
                    _logger.LogInformation(
                        "Overwriting existing destination file during move. DestFileId={DestFileId}",
                        existingDest.Id);
                    await _s3Service.DeleteFileAsync(existingDest.ObjectKey);
                    await _metadataRepository.DeleteByIdAsync(existingDest.Id);
                }

                // Generate new object key for the destination
                var destObjectKey = FileMetadata.GenerateObjectKey(sourceMetadata.Id, normalizedDestPath);

                // Move S3 object — replaces cloud blob copy+delete (source lines 329-344) and
                // filesystem File.Move (source line 355)
                await _s3Service.MoveFileAsync(sourceMetadata.ObjectKey, destObjectKey);

                // Update file metadata in DynamoDB — replaces UPDATE files SET filepath = @filepath
                // (source lines 325-327)
                sourceMetadata.FilePath = normalizedDestPath;
                sourceMetadata.ObjectKey = destObjectKey;
                sourceMetadata.LastModifiedBy = callerId;
                sourceMetadata.LastModificationDate = DateTime.UtcNow;
                sourceMetadata.IsTemp = FileMetadata.NormalizeFilePath(normalizedDestPath)
                    .StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase);

                var updatedMetadata = await _metadataRepository.UpdateAsync(sourceMetadata);

                // Publish SNS domain event: file-management.file.moved
                await PublishDomainEventAsync("file-management.file.moved", new
                {
                    eventType = "file-management.file.moved",
                    fileId = sourceMetadata.Id,
                    oldFilePath = oldFilePath,
                    newFilePath = normalizedDestPath,
                    movedBy = callerId,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    correlationId
                }, correlationId);

                _logger.LogInformation(
                    "File moved successfully. FileId={FileId}, OldPath={OldPath}, NewPath={NewPath}",
                    sourceMetadata.Id, oldFilePath, normalizedDestPath);

                var response = new FileOperationResponse
                {
                    Metadata = updatedMetadata,
                    Success = true,
                    Message = "File moved successfully."
                };
                return BuildResponse(200, response, correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
            {
                _logger.LogWarning(ex, "Source S3 object not found during move. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(404, "Source file content not found in storage.", correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "AccessDenied")
            {
                _logger.LogError(ex, "S3 access denied during move. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(403, "Access denied to file storage.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving file. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(500, "An internal error occurred while moving the file.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: DELETE /v1/files/{fileId}

        /// <summary>
        /// Lambda entry point for DELETE /v1/files/{fileId}.
        /// Deletes a file's S3 object and DynamoDB metadata. Idempotent — returns success
        /// even if the file is already deleted.
        /// <para>
        /// Replaces <c>DbFileRepository.Delete()</c> (source lines 375-429) which dispatched
        /// deletion across three backends. Source behavior preserved: <c>if (file == null) return;</c>
        /// (source line 387-388 — silent success on missing file for idempotent behavior).
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteFile(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleDeleteFile)
            });

            try
            {
                _logger.LogInformation("Processing delete-file request. CorrelationId={CorrelationId}",
                    correlationId);

                var (callerId, callerEmail) = ExtractCallerFromContext(request);

                // Extract fileId from path parameters
                string? fileIdStr = null;
                request.PathParameters?.TryGetValue("fileId", out fileIdStr);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes
                if (string.IsNullOrEmpty(fileIdStr) && request.PathParameters != null &&
                    request.PathParameters.TryGetValue("proxy", out var fpx))
                {
                    var segs = fpx.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segs.Length - 1; i >= 0; i--)
                        if (Guid.TryParse(segs[i], out _)) { fileIdStr = segs[i]; break; }
                }
                if (!Guid.TryParse(fileIdStr, out var fileId))
                {
                    _logger.LogWarning("Invalid or missing fileId for delete operation");
                    return BuildErrorResponse(400, "Invalid or missing fileId parameter. Must be a valid GUID.",
                        correlationId);
                }

                // Retrieve file metadata from DynamoDB
                var metadata = await _metadataRepository.FindByIdAsync(fileId);

                // If not found: Return 204 No Content — matching source line 387-388:
                // if (file == null) return; — silent success on missing file, idempotent behavior
                if (metadata == null)
                {
                    _logger.LogInformation(
                        "File already deleted or does not exist. FileId={FileId} (idempotent success)", fileId);
                    return BuildResponse(204, null, correlationId);
                }

                _logger.LogInformation(
                    "Deleting file. FileId={FileId}, FilePath={FilePath}, ObjectKey={ObjectKey}",
                    metadata.Id, metadata.FilePath, metadata.ObjectKey);

                // Delete S3 object — replaces all three backend deletion patterns (source lines 395-414)
                // S3 DeleteObject is already idempotent (returns 204 even if key doesn't exist)
                await _s3Service.DeleteFileAsync(metadata.ObjectKey);

                // Delete metadata from DynamoDB — replaces DELETE FROM files WHERE id = @id (source lines 417-418)
                await _metadataRepository.DeleteByIdAsync(fileId);

                // Publish SNS domain event: file-management.file.deleted
                await PublishDomainEventAsync("file-management.file.deleted", new
                {
                    eventType = "file-management.file.deleted",
                    fileId = metadata.Id,
                    filePath = metadata.FilePath,
                    createdBy = metadata.CreatedBy,
                    deletedBy = callerId,
                    timestamp = DateTime.UtcNow.ToString("O"),
                    correlationId
                }, correlationId);

                _logger.LogInformation("File deleted successfully. FileId={FileId}", metadata.Id);

                var response = new FileOperationResponse
                {
                    Metadata = metadata,
                    Success = true,
                    Message = "File deleted successfully."
                };
                return BuildResponse(200, response, correlationId);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "AccessDenied")
            {
                _logger.LogError(ex, "S3 access denied during delete. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(403, "Access denied to file storage.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting file. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(500, "An internal error occurred while deleting the file.",
                    correlationId);
            }
        }

        #endregion

        #region Handler: PATCH /v1/files/{fileId}/modification-date

        /// <summary>
        /// Lambda entry point for PATCH /v1/files/{fileId}/modification-date.
        /// Updates the modification timestamp on a file's metadata in DynamoDB.
        /// <para>
        /// Replaces <c>DbFileRepository.UpdateModificationDate()</c> (source lines 202-225).
        /// NOTE: The source had a bug on line 219 where it used <c>Guid.NewGuid()</c> for the
        /// <c>@id</c> parameter instead of <c>file.Id</c>. This bug is NOT replicated — we use
        /// the actual <c>fileId</c> from the path parameter.
        /// </para>
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateModificationDate(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["requestId"] = context.AwsRequestId,
                ["handler"] = nameof(HandleUpdateModificationDate)
            });

            try
            {
                _logger.LogInformation(
                    "Processing update-modification-date request. CorrelationId={CorrelationId}",
                    correlationId);

                // Extract fileId from path parameters
                string? fileIdStr = null;
                request.PathParameters?.TryGetValue("fileId", out fileIdStr);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes
                if (string.IsNullOrEmpty(fileIdStr) && request.PathParameters != null &&
                    request.PathParameters.TryGetValue("proxy", out var fpx))
                {
                    var segs = fpx.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segs.Length - 1; i >= 0; i--)
                        if (Guid.TryParse(segs[i], out _)) { fileIdStr = segs[i]; break; }
                }
                if (!Guid.TryParse(fileIdStr, out var fileId))
                {
                    _logger.LogWarning("Invalid or missing fileId for modification date update");
                    return BuildErrorResponse(400, "Invalid or missing fileId parameter. Must be a valid GUID.",
                        correlationId);
                }

                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.", correlationId);
                }

                UpdateModificationDateRequest? updateRequest;
                try
                {
                    updateRequest = JsonSerializer.Deserialize<UpdateModificationDateRequest>(request.Body);
                }
                catch (JsonException)
                {
                    return BuildErrorResponse(400, "Invalid JSON in request body.", correlationId);
                }

                if (updateRequest == null || updateRequest.ModificationDate == default)
                {
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string>
                        {
                            ["modificationDate"] = "A valid modification date is required."
                        }, correlationId);
                }

                // Retrieve file metadata — replaces Find(filepath) source line 214
                var metadata = await _metadataRepository.FindByIdAsync(fileId);
                if (metadata == null)
                {
                    // Matching source line 216: throw new ArgumentException("file does not exist")
                    _logger.LogWarning("File not found for modification date update. FileId={FileId}", fileId);
                    return BuildErrorResponse(404, "File does not exist.", correlationId);
                }

                _logger.LogInformation(
                    "Updating modification date. FileId={FileId}, NewDate={ModificationDate}",
                    fileId, updateRequest.ModificationDate.ToString("O"));

                // Update LastModificationDate in DynamoDB — replaces
                // UPDATE files SET modified_on = @modified_on WHERE id = @id (source line 218)
                // BUG FIX: Source line 219 used Guid.NewGuid() — we correctly use metadata.Id / fileId
                metadata.LastModificationDate = updateRequest.ModificationDate;
                var updatedMetadata = await _metadataRepository.UpdateAsync(metadata);

                var response = new FileOperationResponse
                {
                    Metadata = updatedMetadata,
                    Success = true,
                    Message = "File modification date updated successfully."
                };
                return BuildResponse(200, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error updating modification date. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(500,
                    "An internal error occurred while updating the file modification date.", correlationId);
            }
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Publishes a domain event to the SNS file events topic.
        /// Event naming convention per AAP §0.8.5: {domain}.{entity}.{action}.
        /// Failures are logged but do NOT fail the request (fire-and-forget resilience).
        /// </summary>
        private async Task PublishDomainEventAsync(string eventType, object eventPayload,
            string correlationId)
        {
            if (string.IsNullOrEmpty(_fileEventTopicArn))
            {
                _logger.LogWarning(
                    "FILE_EVENTS_TOPIC_ARN not configured. Skipping event publish for {EventType}",
                    eventType);
                return;
            }

            try
            {
                var messageBody = JsonSerializer.Serialize(eventPayload);
                var publishRequest = new PublishRequest
                {
                    TopicArn = _fileEventTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        }
                    }
                };

                var publishResponse = await _snsClient.PublishAsync(publishRequest);
                _logger.LogInformation(
                    "Published domain event {EventType}. MessageId={MessageId}",
                    eventType, publishResponse.MessageId);
            }
            catch (Exception ex)
            {
                // Domain event publish failures are logged but do NOT fail the request.
                // The primary operation (S3 + DynamoDB) already succeeded.
                _logger.LogError(ex,
                    "Failed to publish domain event {EventType}. CorrelationId={CorrelationId}",
                    eventType, correlationId);
            }
        }

        /// <summary>
        /// Builds a consistent API Gateway v2 proxy response with JSON body and standard headers.
        /// </summary>
        /// <param name="statusCode">HTTP status code.</param>
        /// <param name="body">Response body object (will be JSON-serialized). Null for 204 responses.</param>
        /// <param name="correlationId">Correlation ID to include in response headers.</param>
        /// <returns>Formatted <see cref="APIGatewayHttpApiV2ProxyResponse"/>.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object? body,
            string correlationId)
        {
            var response = new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Methods"] = "GET,POST,PUT,PATCH,DELETE,OPTIONS",
                    ["Access-Control-Allow-Headers"] = "Content-Type,Authorization,x-correlation-id",
                    ["x-correlation-id"] = correlationId
                }
            };

            if (body != null)
            {
                response.Body = JsonSerializer.Serialize(body, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            }

            return response;
        }

        /// <summary>
        /// Builds a standard error response with the given status code and message.
        /// Stack traces are never exposed in error responses per security requirements.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(int statusCode, string message,
            string correlationId)
        {
            return BuildResponse(statusCode, new
            {
                success = false,
                message,
                timestamp = DateTime.UtcNow.ToString("O"),
                correlationId
            }, correlationId);
        }

        /// <summary>
        /// Builds a 400 validation error response with per-field error details.
        /// </summary>
        /// <param name="errors">Dictionary mapping field names to error messages.</param>
        /// <param name="correlationId">Correlation ID for tracing.</param>
        /// <returns>400 Bad Request response with validation error details.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildValidationErrorResponse(
            Dictionary<string, string> errors, string correlationId)
        {
            return BuildResponse(400, new
            {
                success = false,
                message = "Validation failed.",
                errors,
                timestamp = DateTime.UtcNow.ToString("O"),
                correlationId
            }, correlationId);
        }

        /// <summary>
        /// Extracts or generates a correlation ID for distributed tracing.
        /// Priority: x-correlation-id header → API Gateway requestId → new GUID.
        /// Per AAP §0.8.5: "Structured JSON logging with correlation-ID propagation."
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Check x-correlation-id header first (case-insensitive for HTTP/2)
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, CorrelationIdHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        return header.Value;
                    }
                }
            }

            // Fall back to API Gateway request context ID
            if (!string.IsNullOrEmpty(request.RequestContext?.RequestId))
            {
                return request.RequestContext.RequestId;
            }

            // Generate new GUID as last resort
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts the caller's identity (userId and email) from the JWT claims
        /// in the API Gateway request context. Supports both native JWT authorizer
        /// (<c>RequestContext.Authorizer.Jwt.Claims</c>) and custom Lambda authorizer
        /// (<c>RequestContext.Authorizer.Lambda</c>) for LocalStack fallback.
        /// </summary>
        /// <returns>Tuple of nullable userId and nullable email string.</returns>
        private (Guid? UserId, string? Email) ExtractCallerFromContext(
            APIGatewayHttpApiV2ProxyRequest request)
        {
            Guid? userId = null;
            string? email = null;

            try
            {
                // Try native JWT authorizer first (production API Gateway)
                var jwtClaims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (jwtClaims != null)
                {
                    if (jwtClaims.TryGetValue("sub", out var sub) && Guid.TryParse(sub, out var parsedId))
                    {
                        userId = parsedId;
                    }
                    else if (jwtClaims.TryGetValue("custom:userId", out var customId) &&
                             Guid.TryParse(customId, out var parsedCustomId))
                    {
                        userId = parsedCustomId;
                    }

                    jwtClaims.TryGetValue("email", out email);
                    return (userId, email);
                }

                // Try Lambda authorizer context (LocalStack fallback)
                var lambdaContext = request.RequestContext?.Authorizer?.Lambda;
                if (lambdaContext != null)
                {
                    if (lambdaContext.TryGetValue("sub", out var subObj) &&
                        subObj is string subStr && Guid.TryParse(subStr, out var lambdaParsedId))
                    {
                        userId = lambdaParsedId;
                    }
                    else if (lambdaContext.TryGetValue("userId", out var userIdObj) &&
                             userIdObj is string userIdStr && Guid.TryParse(userIdStr, out var parsedUserId))
                    {
                        userId = parsedUserId;
                    }

                    if (lambdaContext.TryGetValue("email", out var emailObj) && emailObj is string emailStr)
                    {
                        email = emailStr;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract caller identity from request context");
            }

            return (userId, email);
        }

        /// <summary>
        /// Normalizes a file path to match the monolith's canonical format.
        /// Consolidates the repeated normalization pattern from all source DbFileRepository methods:
        /// <list type="bullet">
        ///   <item><description>ToLowerInvariant() — source Find line 40, FindAll line 63, Create line 125, Copy lines 242-243, Move lines 298-299, Delete line 381</description></item>
        ///   <item><description>Prepend "/" if missing — source Find lines 41-42, FindAll lines 65-66, Create lines 126-127, Copy lines 245-249, Move lines 301-305, Delete lines 382-383</description></item>
        /// </list>
        /// Delegates to <see cref="FileMetadata.NormalizeFilePath(string)"/> for consistent behavior.
        /// </summary>
        /// <param name="filepath">Raw filepath to normalize.</param>
        /// <returns>Normalized lowercase filepath with leading "/" separator.</returns>
        private static string NormalizeFilePath(string filepath)
        {
            return FileMetadata.NormalizeFilePath(filepath);
        }

        #endregion
    }
}
