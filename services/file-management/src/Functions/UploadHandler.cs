// ---------------------------------------------------------------------------
// UploadHandler.cs — AWS Lambda handler for file upload operations in the
// File Management bounded-context service.
//
// Replaces:
//   DbFileRepository.Create()        → presigned S3 PUT URL + DynamoDB metadata
//   DbFileRepository.CreateTempFile() → temp presigned URL with TTL metadata
//   UserFileService.CreateUserFile()  → finalize temp file to permanent location
//
// ZERO references to WebVella.Erp.* namespaces, ZERO PostgreSQL/Npgsql,
// ZERO Storage.Net — pure AWS serverless implementation.
// ---------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.FileManagement.DataAccess;
using WebVellaErp.FileManagement.Models;
using WebVellaErp.FileManagement.Services;

// NOTE: [assembly: LambdaSerializer] is declared in DownloadHandler.cs — only one per assembly.

namespace WebVellaErp.FileManagement.Functions
{
    #region Request/Response DTOs

    /// <summary>
    /// API-facing request DTO for generating a presigned upload URL.
    /// Clients send this to POST /v1/files/upload-url or POST /v1/files/temp-upload-url.
    /// Maps to internal <see cref="UploadFileRequest"/> for metadata construction.
    /// </summary>
    public record GenerateUploadUrlRequest
    {
        /// <summary>Original filename (required). Used for extension extraction and path generation.</summary>
        [JsonPropertyName("fileName")]
        public string FileName { get; init; } = string.Empty;

        /// <summary>MIME content type (optional). Auto-detected from filename if not provided.</summary>
        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        /// <summary>File size in bytes (required). Used for presigned URL size constraint.</summary>
        [JsonPropertyName("contentLength")]
        public long ContentLength { get; init; }

        /// <summary>Whether this is a temporary file upload (optional, default false).</summary>
        [JsonPropertyName("isTemp")]
        public bool IsTemp { get; init; }
    }

    /// <summary>
    /// API-facing request DTO for finalizing a temp file to permanent storage.
    /// Clients send this to POST /v1/files/{fileId}/finalize.
    /// Replaces parameters from <c>UserFileService.CreateUserFile(string path, string alt, string caption)</c>.
    /// </summary>
    public record FinalizeUserFileRequest
    {
        /// <summary>Alt text for the file (from UserFileService.CreateUserFile alt parameter).</summary>
        [JsonPropertyName("alt")]
        public string? Alt { get; init; }

        /// <summary>Caption text for the file (from UserFileService.CreateUserFile caption parameter).</summary>
        [JsonPropertyName("caption")]
        public string? Caption { get; init; }

        /// <summary>
        /// Optional permanent destination path. Defaults to /file/{fileId}/{filename}
        /// matching source UserFileService.cs line 91.
        /// </summary>
        [JsonPropertyName("destinationPath")]
        public string? DestinationPath { get; init; }
    }

    #endregion

    /// <summary>
    /// AWS Lambda entry point class for all file upload operations in the File Management service.
    /// <para>
    /// Provides four handler methods mapped to HTTP API Gateway v2 routes:
    /// <list type="bullet">
    ///   <item><description><c>HandleGenerateUploadUrl</c> — POST /v1/files/upload-url (replaces DbFileRepository.Create byte upload)</description></item>
    ///   <item><description><c>HandleConfirmUpload</c> — POST /v1/files/{fileId}/confirm (replaces DbFileRepository.Create commit phase)</description></item>
    ///   <item><description><c>HandleCreateTempUploadUrl</c> — POST /v1/files/temp-upload-url (replaces DbFileRepository.CreateTempFile)</description></item>
    ///   <item><description><c>HandleFinalizeUserFile</c> — POST /v1/files/{fileId}/finalize (replaces UserFileService.CreateUserFile)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// All methods use presigned S3 PUT URLs for direct client-to-S3 uploads instead of
    /// proxying bytes through the API. Metadata is persisted in DynamoDB. Domain events
    /// are published to SNS for cross-service notifications.
    /// </para>
    /// </summary>
    public class UploadHandler
    {
        #region Fields & Constants

        private readonly IS3Service _s3Service;
        private readonly IFileMetadataRepository _metadataRepository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<UploadHandler> _logger;

        /// <summary>SNS topic ARN for file domain events from FILE_EVENTS_TOPIC_ARN env var.</summary>
        private readonly string _fileEventTopicArn;

        /// <summary>Default presigned URL expiration in minutes.</summary>
        private const int DefaultPresignedUrlExpirationMinutes = 60;

        /// <summary>Header name for correlation ID propagation per AAP §0.8.5.</summary>
        private const string CorrelationIdHeader = "x-correlation-id";

        /// <summary>Default TTL for temporary file metadata in DynamoDB (24 hours in seconds).</summary>
        private const long DefaultTempFileTtlSeconds = 86400;

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for the AWS Lambda runtime.
        /// Builds a DI <see cref="ServiceCollection"/>, configures all services
        /// (AWS SDK clients, IS3Service, IFileMetadataRepository), and resolves dependencies.
        /// All SDK clients respect <c>AWS_ENDPOINT_URL</c> for LocalStack compatibility.
        /// </summary>
        public UploadHandler()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            var provider = services.BuildServiceProvider();

            _s3Service = provider.GetRequiredService<IS3Service>();
            _metadataRepository = provider.GetRequiredService<IFileMetadataRepository>();
            _snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILogger<UploadHandler>>();
            _fileEventTopicArn = Environment.GetEnvironmentVariable("FILE_EVENTS_TOPIC_ARN") ?? string.Empty;
        }

        /// <summary>
        /// Constructor accepting an <see cref="IServiceProvider"/> for unit testing.
        /// Allows test fixtures to inject mocked services without building the full DI container.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider with all required dependencies.</param>
        public UploadHandler(IServiceProvider serviceProvider)
        {
            _s3Service = serviceProvider.GetRequiredService<IS3Service>();
            _metadataRepository = serviceProvider.GetRequiredService<IFileMetadataRepository>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILogger<UploadHandler>>();
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

            // Structured JSON logging per AAP §0.8.5
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

        #region Handler: POST /v1/files/upload-url

        /// <summary>
        /// Lambda entry point for POST /v1/files/upload-url.
        /// Generates a presigned S3 PUT URL for direct client-to-S3 upload and creates a
        /// preliminary metadata record in DynamoDB.
        /// <para>
        /// Replaces <c>DbFileRepository.Create(string filepath, byte[] buffer, ...)</c> (source lines 119-200)
        /// where the monolith received bytes directly. In the serverless architecture, clients upload
        /// directly to S3 via the presigned URL.
        /// </para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway proxy response with presigned upload URL and file metadata.</returns>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        /// </summary>
        /// <summary>Lazily-initialized DownloadHandler for delegating GET/DELETE/PUT requests.</summary>
        private DownloadHandler? _downloadHandler;
        private DownloadHandler GetDownloadHandler()
        {
            _downloadHandler ??= new DownloadHandler();
            return _downloadHandler;
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            // Delegate GET, DELETE, PUT to the DownloadHandler which handles read/delete/update ops
            if (method == "GET" || method == "DELETE" || method == "PUT")
                return await GetDownloadHandler().FunctionHandler(request, context);

            // POST routes — upload-related operations
            if (path.Contains("/confirm"))
                return await HandleConfirmUpload(request, context);
            if (path.Contains("/temp"))
                return await HandleCreateTempUploadUrl(request, context);
            if (path.Contains("/finalize"))
                return await HandleFinalizeUserFile(request, context);

            // Default: generate upload URL
            return await HandleGenerateUploadUrl(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGenerateUploadUrl(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["handler"] = "HandleGenerateUploadUrl",
                ["requestId"] = context.AwsRequestId
            });

            _logger.LogInformation(
                "Generating upload URL. CorrelationId={CorrelationId}",
                correlationId);

            try
            {
                // Deserialize API-facing request DTO
                if (string.IsNullOrEmpty(request.Body))
                {
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["body"] = "Request body is required." },
                        correlationId);
                }

                var apiRequest = JsonSerializer.Deserialize<GenerateUploadUrlRequest>(request.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (apiRequest == null)
                {
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["body"] = "Failed to parse request body." },
                        correlationId);
                }

                // Input validation (pre-hook equivalent per AAP §0.7.2)
                var validationErrors = new Dictionary<string, string>();
                if (string.IsNullOrWhiteSpace(apiRequest.FileName))
                {
                    // Mirrors source DbFileRepository.Create line 121-122:
                    // if (string.IsNullOrWhiteSpace(filepath)) throw new ArgumentException("filepath cannot be null or empty")
                    validationErrors["fileName"] = "File name is required.";
                }
                if (apiRequest.ContentLength <= 0)
                {
                    validationErrors["contentLength"] = "Content length must be a positive value.";
                }
                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning(
                        "Validation failed for upload URL request. Errors={ErrorCount}, CorrelationId={CorrelationId}",
                        validationErrors.Count, correlationId);
                    return BuildValidationErrorResponse(validationErrors, correlationId);
                }

                // Extract caller identity from JWT claims (replaces SecurityContext.CurrentUser)
                var (callerId, callerEmail) = ExtractCallerFromContext(request);

                // Generate file ID matching source DbFileRepository.Create line 155: Guid.NewGuid()
                var fileId = Guid.NewGuid();

                // Extract extension for S3 key generation and type classification
                var extension = Path.GetExtension(apiRequest.FileName);

                // Normalize file path to lowercase matching source DbFileRepository.Create line 125
                var normalizedFileName = FileMetadata.NormalizeFilePath(apiRequest.FileName);

                // Build internal request representation to map API fields to domain model
                // Uses all UploadFileRequest members_accessed per schema requirements
                var internalRequest = new UploadFileRequest
                {
                    FilePath = normalizedFileName,
                    ContentType = apiRequest.ContentType ?? string.Empty,
                    Size = apiRequest.ContentLength,
                    CreatedBy = callerId,
                    IsTemp = apiRequest.IsTemp,
                    FileName = apiRequest.FileName,
                    Extension = extension
                };

                // Auto-detect content type if not provided (replaces MimeMapping.MimeUtility.GetMimeMapping)
                var contentType = !string.IsNullOrEmpty(internalRequest.ContentType)
                    ? internalRequest.ContentType
                    : await _s3Service.DetectContentTypeAsync(internalRequest.FileName ?? apiRequest.FileName);

                // Classify file type: image/video/audio/document/other
                // Matching source UserFileService.cs lines 70-89
                var fileType = _s3Service.ClassifyFileType(contentType, internalRequest.Extension ?? extension);

                // Generate S3 object key using sharding pattern from GetBlobPath()
                string objectKey;
                if (internalRequest.IsTemp)
                {
                    // Temp file key: tmp/{depth1}/{depth2}/{fileId}{extension}
                    // Matching source CreateTempFile() lines 437-449
                    objectKey = _s3Service.GenerateTempObjectKey(fileId, internalRequest.FileName ?? apiRequest.FileName, internalRequest.Extension);
                }
                else
                {
                    // Permanent file key: {depth1}/{depth2}/{fileId}{extension}
                    // Matching source GetBlobPath() lines 496-508
                    objectKey = _s3Service.GenerateObjectKey(fileId, internalRequest.FilePath);
                }

                // Generate presigned PUT URL for direct client-to-S3 upload
                var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
                    objectKey,
                    contentType,
                    internalRequest.Size,
                    DefaultPresignedUrlExpirationMinutes);

                // Create preliminary file metadata record in DynamoDB
                // Matching source DbFileRepository.Create lines 155-175 (PostgreSQL INSERT)
                var now = DateTime.UtcNow;
                var metadata = new FileMetadata
                {
                    Id = fileId,
                    FilePath = internalRequest.FilePath,
                    ObjectKey = objectKey,
                    ContentType = contentType,
                    Size = internalRequest.Size,
                    CreatedBy = internalRequest.CreatedBy ?? callerId,
                    CreatedOn = now,
                    LastModifiedBy = internalRequest.CreatedBy ?? callerId,
                    LastModificationDate = now,
                    IsTemp = internalRequest.IsTemp
                };

                // Set TTL for temp files to enable auto-expiration
                // Replaces CleanupExpiredTempFiles() from source lines 455-469
                if (internalRequest.IsTemp)
                {
                    var ttlEpoch = new DateTimeOffset(now.AddSeconds(DefaultTempFileTtlSeconds)).ToUnixTimeSeconds();
                    metadata.Ttl = ttlEpoch;
                }

                await _metadataRepository.CreateAsync(metadata);

                // NEVER log presigned URLs — they contain authentication tokens (security requirement)
                _logger.LogInformation(
                    "Upload URL generated. FileId={FileId}, ObjectKey={ObjectKey}, ContentType={ContentType}, " +
                    "Size={Size}, IsTemp={IsTemp}, FileType={FileType}, CorrelationId={CorrelationId}",
                    fileId, objectKey, contentType, internalRequest.Size, internalRequest.IsTemp, fileType, correlationId);

                // Build response with UploadFileResponse DTO
                var expiresAt = now.AddMinutes(DefaultPresignedUrlExpirationMinutes);
                var responseBody = new UploadFileResponse
                {
                    FileId = fileId,
                    PresignedUrl = presignedUrl,
                    ObjectKey = objectKey,
                    ExpiresAt = expiresAt,
                    Metadata = metadata,
                    Success = true,
                    Message = "Upload URL generated successfully."
                };

                return BuildResponse(200, responseBody, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "Invalid argument in upload URL generation. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(400, ex.Message, correlationId);
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex,
                    "S3 error during upload URL generation. CorrelationId={CorrelationId}",
                    correlationId);
                var statusCode = ex.ErrorCode == "AccessDenied" ? 403 : 500;
                return BuildErrorResponse(statusCode, "Failed to generate upload URL.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error generating upload URL. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred.", correlationId);
            }
        }

        #endregion

        #region Handler: POST /v1/files/{fileId}/confirm

        /// <summary>
        /// Lambda entry point for POST /v1/files/{fileId}/confirm.
        /// Confirms that a file was successfully uploaded to S3 via the presigned URL and
        /// activates the metadata record in DynamoDB.
        /// <para>
        /// Replaces the commit phase of <c>DbFileRepository.Create()</c> (source lines 132-199)
        /// where after writing bytes, the metadata record was committed via
        /// <c>connection.CommitTransaction()</c> (source line 190).
        /// </para>
        /// <para>
        /// Idempotent: re-confirming an already-confirmed file returns success without
        /// re-publishing the domain event, per AAP §0.8.5.
        /// </para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with fileId path parameter.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway proxy response with confirmed file metadata.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleConfirmUpload(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["handler"] = "HandleConfirmUpload",
                ["requestId"] = context.AwsRequestId
            });

            _logger.LogInformation(
                "Confirming file upload. CorrelationId={CorrelationId}",
                correlationId);

            try
            {
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
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["fileId"] = "Valid file ID is required in path." },
                        correlationId);
                }

                // Retrieve metadata from DynamoDB
                var metadata = await _metadataRepository.FindByIdAsync(fileId);
                if (metadata == null)
                {
                    _logger.LogWarning(
                        "File metadata not found for confirmation. FileId={FileId}, CorrelationId={CorrelationId}",
                        fileId, correlationId);
                    return BuildErrorResponse(404, $"File metadata not found for ID '{fileId}'.", correlationId);
                }

                // Verify the file actually exists in S3 (client must have completed the presigned URL upload)
                var fileExists = await _s3Service.FileExistsAsync(metadata.ObjectKey);
                if (!fileExists)
                {
                    _logger.LogWarning(
                        "S3 object not yet uploaded. FileId={FileId}, ObjectKey={ObjectKey}, CorrelationId={CorrelationId}",
                        fileId, metadata.ObjectKey, correlationId);
                    return BuildErrorResponse(409,
                        "File has not been uploaded to S3 yet. Complete the presigned URL upload first.",
                        correlationId);
                }

                // Check for idempotent re-confirmation: if metadata was already confirmed
                // (LastModificationDate > CreatedOn), return success without re-publishing event
                var alreadyConfirmed = metadata.LastModificationDate > metadata.CreatedOn;
                if (!alreadyConfirmed)
                {
                    // Update metadata to mark as confirmed (analogous to source connection.CommitTransaction())
                    metadata.LastModificationDate = DateTime.UtcNow;
                    metadata.LastModifiedBy = ExtractCallerFromContext(request).UserId;
                    await _metadataRepository.UpdateAsync(metadata);

                    // Publish SNS domain event: file-management.file.created
                    // Replaces synchronous post-create hook pattern from monolith (AAP §0.7.2)
                    await PublishDomainEventAsync(
                        "file-management.file.created",
                        new
                        {
                            eventType = "file-management.file.created",
                            fileId = metadata.Id,
                            filePath = metadata.FilePath,
                            objectKey = metadata.ObjectKey,
                            contentType = metadata.ContentType,
                            size = metadata.Size,
                            createdBy = metadata.CreatedBy,
                            timestamp = DateTime.UtcNow.ToString("O"),
                            correlationId
                        },
                        correlationId);
                }

                _logger.LogInformation(
                    "Upload confirmed. FileId={FileId}, ObjectKey={ObjectKey}, " +
                    "AlreadyConfirmed={AlreadyConfirmed}, CorrelationId={CorrelationId}",
                    fileId, metadata.ObjectKey, alreadyConfirmed, correlationId);

                var responseBody = new FileOperationResponse
                {
                    Metadata = metadata,
                    Success = true,
                    Message = alreadyConfirmed
                        ? "File was already confirmed."
                        : "File upload confirmed successfully."
                };

                return BuildResponse(200, responseBody, correlationId);
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex,
                    "S3 error during upload confirmation. CorrelationId={CorrelationId}",
                    correlationId);
                var statusCode = ex.ErrorCode switch
                {
                    "NoSuchKey" => 404,
                    "AccessDenied" => 403,
                    _ => 500
                };
                return BuildErrorResponse(statusCode, "Failed to confirm file upload.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error confirming upload. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred.", correlationId);
            }
        }

        #endregion

        #region Handler: POST /v1/files/temp-upload-url

        /// <summary>
        /// Lambda entry point for POST /v1/files/temp-upload-url.
        /// Generates a presigned S3 PUT URL for a temporary file upload.
        /// <para>
        /// Replaces <c>DbFileRepository.CreateTempFile()</c> (source lines 437-449):
        /// <code>
        /// string section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
        /// var tmpFilePath = "/" + "tmp" + "/" + section + "/" + filename + extension;
        /// return Create(tmpFilePath, buffer, DateTime.UtcNow, null);
        /// </code>
        /// </para>
        /// <para>
        /// Key differences from permanent upload:
        /// - S3 key uses <c>tmp/</c> prefix via <see cref="IS3Service.GenerateTempObjectKey"/>
        /// - DynamoDB metadata has TTL set for auto-expiration (24h default)
        /// - No user association (createdBy = null, matching source line 448)
        /// </para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway proxy response with presigned upload URL for temp file.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateTempUploadUrl(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["handler"] = "HandleCreateTempUploadUrl",
                ["requestId"] = context.AwsRequestId
            });

            _logger.LogInformation(
                "Generating temp upload URL. CorrelationId={CorrelationId}",
                correlationId);

            try
            {
                // Deserialize request body
                GenerateUploadUrlRequest? dto = null;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    dto = JsonSerializer.Deserialize<GenerateUploadUrlRequest>(request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }

                if (dto == null || string.IsNullOrWhiteSpace(dto.FileName))
                {
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["fileName"] = "fileName is required." },
                        correlationId);
                }

                if (dto.ContentLength <= 0)
                {
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["contentLength"] = "contentLength must be a positive number." },
                        correlationId);
                }

                // Generate a new file ID for this temp file
                var fileId = Guid.NewGuid();

                // Extension normalization matching source lines 439-444:
                //   extension = extension.Trim().ToLowerInvariant();
                //   if (!extension.StartsWith(".")) extension = "." + extension;
                var rawExtension = Path.GetExtension(dto.FileName);
                string? normalizedExtension = null;
                if (!string.IsNullOrWhiteSpace(rawExtension))
                {
                    normalizedExtension = rawExtension.Trim().ToLowerInvariant();
                    if (!normalizedExtension.StartsWith('.'))
                        normalizedExtension = "." + normalizedExtension;
                }

                var fileName = Path.GetFileNameWithoutExtension(dto.FileName);

                // Auto-detect content type if not provided
                var contentType = dto.ContentType;
                if (string.IsNullOrWhiteSpace(contentType))
                {
                    contentType = await _s3Service.DetectContentTypeAsync(dto.FileName);
                }

                // Classify file type (image/video/audio/document/other) matching source lines 70-89
                var fileType = _s3Service.ClassifyFileType(contentType, normalizedExtension ?? string.Empty);

                // Generate S3 key for temp file using tmp/ prefix
                var objectKey = _s3Service.GenerateTempObjectKey(fileId, fileName, normalizedExtension);

                // Generate temp file path matching source line 447:
                //   "/" + "tmp" + "/" + section + "/" + filename + extension
                // section is GUID without hyphens (matching source line 446)
                var section = fileId.ToString("N"); // ToString("N") = no hyphens, already lowercase
                var tempFilePath = $"/{FileMetadata.TmpFolderName}/{section}/{fileName}{normalizedExtension ?? string.Empty}";

                // Generate presigned PUT URL
                var presignedUrl = await _s3Service.GeneratePresignedUploadUrlAsync(
                    objectKey, contentType, dto.ContentLength, DefaultPresignedUrlExpirationMinutes);

                // Create DynamoDB metadata with TTL for auto-expiration
                // createdBy = null matching source line 448: Create(tmpFilePath, buffer, DateTime.UtcNow, null)
                var metadata = new FileMetadata
                {
                    Id = fileId,
                    FilePath = FileMetadata.NormalizeFilePath(tempFilePath),
                    ObjectKey = objectKey,
                    ContentType = contentType,
                    Size = dto.ContentLength,
                    CreatedBy = null, // Temp files have no user association (source line 448)
                    CreatedOn = DateTime.UtcNow,
                    LastModifiedBy = null,
                    LastModificationDate = DateTime.UtcNow,
                    IsTemp = true,
                    Ttl = DateTimeOffset.UtcNow.AddSeconds(DefaultTempFileTtlSeconds).ToUnixTimeSeconds()
                };

                await _metadataRepository.CreateAsync(metadata);

                _logger.LogInformation(
                    "Temp upload URL generated. FileId={FileId}, ObjectKey={ObjectKey}, " +
                    "ContentType={ContentType}, CorrelationId={CorrelationId}",
                    fileId, objectKey, contentType, correlationId);

                var responseBody = new UploadFileResponse
                {
                    FileId = fileId,
                    PresignedUrl = presignedUrl,
                    ObjectKey = objectKey,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(DefaultPresignedUrlExpirationMinutes),
                    Metadata = metadata,
                    Success = true,
                    Message = "Temporary upload URL generated successfully."
                };

                return BuildResponse(200, responseBody, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "Validation error for temp upload URL. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(400, ex.Message, correlationId);
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex,
                    "S3 error generating temp upload URL. CorrelationId={CorrelationId}",
                    correlationId);
                var statusCode = ex.ErrorCode == "AccessDenied" ? 403 : 500;
                return BuildErrorResponse(statusCode, "Failed to generate temp upload URL.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error generating temp upload URL. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred.", correlationId);
            }
        }

        #endregion

        #region Handler: POST /v1/files/{fileId}/finalize

        /// <summary>
        /// Lambda entry point for POST /v1/files/{fileId}/finalize.
        /// Finalizes a temp file by moving it from the temp S3 prefix to a permanent
        /// location and updating the DynamoDB metadata.
        /// <para>
        /// Replaces <c>UserFileService.CreateUserFile()</c> (source lines 51-118):
        /// <code>
        /// var newFilePath = $"/file/{newFileId}/{Path.GetFileName(path)}";
        /// var file = Fs.Move(path, newFilePath, false);
        /// // Create user_file entity record
        /// var response = RecMan.CreateRecord("user_file", userFileRecord);
        /// </code>
        /// </para>
        /// <para>
        /// Idempotent: re-finalizing an already-finalized file returns the existing
        /// metadata without performing the move or re-publishing events.
        /// </para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request with fileId path parameter and body.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway proxy response with finalized file metadata.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleFinalizeUserFile(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["correlationId"] = correlationId,
                ["handler"] = "HandleFinalizeUserFile",
                ["requestId"] = context.AwsRequestId
            });

            _logger.LogInformation(
                "Finalizing user file. CorrelationId={CorrelationId}",
                correlationId);

            try
            {
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
                    return BuildValidationErrorResponse(
                        new Dictionary<string, string> { ["fileId"] = "Valid file ID is required in path." },
                        correlationId);
                }

                // Deserialize optional request body for alt/caption/destination
                FinalizeUserFileRequest? dto = null;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    dto = JsonSerializer.Deserialize<FinalizeUserFileRequest>(request.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }

                // Retrieve the existing file metadata from DynamoDB
                var metadata = await _metadataRepository.FindByIdAsync(fileId);
                if (metadata == null)
                {
                    _logger.LogWarning(
                        "File metadata not found for finalization. FileId={FileId}, CorrelationId={CorrelationId}",
                        fileId, correlationId);
                    return BuildErrorResponse(404, $"File metadata not found for ID '{fileId}'.", correlationId);
                }

                // Idempotency: if the file has already been finalized (no longer temp), return it
                if (!metadata.IsTemp)
                {
                    _logger.LogInformation(
                        "File already finalized. FileId={FileId}, CorrelationId={CorrelationId}",
                        fileId, correlationId);
                    return BuildResponse(200, new FileOperationResponse
                    {
                        Metadata = metadata,
                        Success = true,
                        Message = "File was already finalized."
                    }, correlationId);
                }

                // Verify the file exists in S3 before attempting move
                var fileExists = await _s3Service.FileExistsAsync(metadata.ObjectKey);
                if (!fileExists)
                {
                    _logger.LogWarning(
                        "S3 object not found for finalization. FileId={FileId}, ObjectKey={ObjectKey}, " +
                        "CorrelationId={CorrelationId}",
                        fileId, metadata.ObjectKey, correlationId);
                    return BuildErrorResponse(409,
                        "File has not been uploaded to S3 yet. Complete upload and confirmation first.",
                        correlationId);
                }

                var (callerId, _) = ExtractCallerFromContext(request);
                var previousPath = metadata.FilePath;
                var previousObjectKey = metadata.ObjectKey;

                // Compute permanent file path matching source line 91:
                //   var newFilePath = $"/file/{newFileId}/{Path.GetFileName(path)}";
                var fileName = Path.GetFileName(metadata.FilePath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = $"{fileId}";
                }

                var permanentFilePath = dto?.DestinationPath;
                if (string.IsNullOrWhiteSpace(permanentFilePath))
                {
                    permanentFilePath = $"/file/{fileId}/{fileName}";
                }
                permanentFilePath = FileMetadata.NormalizeFilePath(permanentFilePath);

                // Compute file size in KB matching source line 65:
                //   Math.Round(((decimal)tempFile.GetBytes().Length / 1024), 2)
                var sizeInKb = Math.Round((decimal)metadata.Size / 1024, 2);

                // Generate permanent S3 object key
                var permanentObjectKey = _s3Service.GenerateObjectKey(fileId, permanentFilePath);

                // Move S3 object from temp key to permanent key
                // Replaces source line 98: Fs.Move(path, newFilePath, false)
                _logger.LogInformation(
                    "Moving file from temp to permanent. FileId={FileId}, " +
                    "SourceKey={SourceKey}, DestKey={DestKey}, CorrelationId={CorrelationId}",
                    fileId, previousObjectKey, permanentObjectKey, correlationId);

                await _s3Service.MoveFileAsync(previousObjectKey, permanentObjectKey);

                // Update metadata in DynamoDB to reflect permanent location
                metadata.ObjectKey = permanentObjectKey;
                metadata.FilePath = permanentFilePath;
                metadata.IsTemp = false;
                metadata.Ttl = 0; // Remove TTL — permanent files do not expire
                metadata.LastModifiedBy = callerId;
                metadata.LastModificationDate = DateTime.UtcNow;

                await _metadataRepository.UpdateAsync(metadata);

                // Publish SNS domain event: file-management.file.finalized
                // Replaces the monolith's record creation + hook pattern from UserFileService.CreateUserFile()
                await PublishDomainEventAsync(
                    "file-management.file.finalized",
                    new
                    {
                        eventType = "file-management.file.finalized",
                        fileId = metadata.Id,
                        filePath = metadata.FilePath,
                        previousPath,
                        objectKey = metadata.ObjectKey,
                        contentType = metadata.ContentType,
                        size = metadata.Size,
                        sizeInKb,
                        alt = dto?.Alt,
                        caption = dto?.Caption,
                        createdBy = metadata.CreatedBy,
                        finalizedBy = callerId,
                        timestamp = DateTime.UtcNow.ToString("O"),
                        correlationId
                    },
                    correlationId);

                _logger.LogInformation(
                    "File finalized. FileId={FileId}, PermanentPath={PermanentPath}, " +
                    "SizeKB={SizeKB}, CorrelationId={CorrelationId}",
                    fileId, permanentFilePath, sizeInKb, correlationId);

                var responseBody = new FileOperationResponse
                {
                    Metadata = metadata,
                    Success = true,
                    Message = "File finalized successfully."
                };

                return BuildResponse(200, responseBody, correlationId);
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex,
                    "S3 error during file finalization. CorrelationId={CorrelationId}",
                    correlationId);

                // Map S3 errors. NoSuchKey→404, AccessDenied→403
                // Matches source error handling where DbFileRepository threw on missing files
                var statusCode = ex.ErrorCode switch
                {
                    "NoSuchKey" => 404,
                    "AccessDenied" => 403,
                    _ => 500
                };
                return BuildErrorResponse(statusCode, "Failed to finalize file.", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error finalizing file. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildErrorResponse(500, "An internal error occurred.", correlationId);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Publishes a domain event to the SNS file events topic.
        /// Event naming follows AAP §0.8.5 convention: {domain}.{entity}.{action}.
        /// Catches and logs errors without failing the calling request.
        /// </summary>
        /// <param name="eventType">The event type string, e.g. "file-management.file.created".</param>
        /// <param name="eventPayload">The event payload object to serialize as JSON.</param>
        /// <param name="correlationId">The correlation ID for tracing.</param>
        private async Task PublishDomainEventAsync(string eventType, object eventPayload, string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_fileEventTopicArn))
            {
                _logger.LogWarning(
                    "FILE_EVENTS_TOPIC_ARN not configured. Skipping domain event {EventType}. " +
                    "CorrelationId={CorrelationId}",
                    eventType, correlationId);
                return;
            }

            try
            {
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var messageBody = JsonSerializer.Serialize(eventPayload, jsonOptions);

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

                var response = await _snsClient.PublishAsync(publishRequest);

                _logger.LogInformation(
                    "Domain event published. EventType={EventType}, MessageId={MessageId}, " +
                    "CorrelationId={CorrelationId}",
                    eventType, response.MessageId, correlationId);
            }
            catch (Exception ex)
            {
                // Log but do not fail the calling request — event publishing is best-effort
                _logger.LogError(ex,
                    "Failed to publish domain event {EventType}. CorrelationId={CorrelationId}",
                    eventType, correlationId);
            }
        }

        /// <summary>
        /// Builds a standard API Gateway HTTP API v2 proxy response with JSON body,
        /// CORS headers, and camelCase property naming.
        /// </summary>
        /// <param name="statusCode">HTTP status code.</param>
        /// <param name="body">Response body object to serialize.</param>
        /// <param name="correlationId">Correlation ID to include in response headers.</param>
        /// <returns>Fully formed API Gateway proxy response.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode, object body, string? correlationId = null)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Access-Control-Allow-Origin"] = "*",
                ["Access-Control-Allow-Methods"] = "GET,POST,PUT,PATCH,DELETE,OPTIONS",
                ["Access-Control-Allow-Headers"] = "Content-Type,Authorization,x-correlation-id"
            };

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                headers["x-correlation-id"] = correlationId;
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = headers,
                Body = JsonSerializer.Serialize(body, jsonOptions)
            };
        }

        /// <summary>
        /// Builds a structured error response with success=false, message, timestamp,
        /// and correlationId. Never exposes stack traces in production.
        /// </summary>
        /// <param name="statusCode">HTTP status code (4xx or 5xx).</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="correlationId">Correlation ID for tracing.</param>
        /// <returns>API Gateway proxy error response.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode, string message, string correlationId)
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
        /// Builds a 400 validation error response with per-field error messages.
        /// </summary>
        /// <param name="errors">Dictionary mapping field names to error descriptions.</param>
        /// <param name="correlationId">Correlation ID for tracing.</param>
        /// <returns>API Gateway proxy validation error response.</returns>
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
        /// Extracts or generates a correlation ID from the request.
        /// Priority: x-correlation-id header → API Gateway requestId → new GUID.
        /// Per AAP §0.8.5: "Structured JSON logging with correlation-ID propagation."
        /// </summary>
        /// <param name="request">API Gateway proxy request.</param>
        /// <returns>A correlation ID string (never null).</returns>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Check x-correlation-id header (case-insensitive)
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, CorrelationIdHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(header.Value))
                            return header.Value;
                    }
                }
            }

            // Fall back to API Gateway request context request ID
            if (!string.IsNullOrWhiteSpace(request.RequestContext?.RequestId))
                return request.RequestContext.RequestId;

            // Final fallback: generate a new GUID
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts authenticated caller identity from JWT claims populated by
        /// the API Gateway JWT authorizer (or custom Lambda authorizer fallback).
        /// Replaces <c>SecurityContext.CurrentUser</c> and cookie-based identity from the monolith.
        /// </summary>
        /// <param name="request">API Gateway proxy request.</param>
        /// <returns>Tuple of (userId GUID, email string), both nullable.</returns>
        private static (Guid? UserId, string? Email) ExtractCallerFromContext(
            APIGatewayHttpApiV2ProxyRequest request)
        {
            Guid? userId = null;
            string? email = null;

            // Try native HTTP API Gateway JWT authorizer claims
            var jwtClaims = request.RequestContext?.Authorizer?.Jwt?.Claims;
            if (jwtClaims != null)
            {
                // Try "sub" claim first (standard JWT), then "custom:userId"
                if (jwtClaims.TryGetValue("sub", out var sub) && Guid.TryParse(sub, out var subGuid))
                {
                    userId = subGuid;
                }
                else if (jwtClaims.TryGetValue("custom:userId", out var customUserId) &&
                         Guid.TryParse(customUserId, out var customGuid))
                {
                    userId = customGuid;
                }

                if (jwtClaims.TryGetValue("email", out var emailClaim))
                {
                    email = emailClaim;
                }
            }

            // Fallback: Lambda authorizer context (for LocalStack compatibility)
            if (userId == null && request.RequestContext?.Authorizer?.Lambda != null)
            {
                var lambdaAuth = request.RequestContext.Authorizer.Lambda;
                if (lambdaAuth.TryGetValue("userId", out var lambdaUserId))
                {
                    var userIdStr = lambdaUserId?.ToString();
                    if (Guid.TryParse(userIdStr, out var lambdaGuid))
                    {
                        userId = lambdaGuid;
                    }
                }
                if (lambdaAuth.TryGetValue("email", out var lambdaEmail))
                {
                    email = lambdaEmail?.ToString();
                }
            }

            return (userId, email);
        }

        #endregion
    }
}
