// =============================================================================
// WebVella ERP — Core Platform Microservice
// RecordGrpcServiceImpl: gRPC service for dynamic record CRUD operations
// =============================================================================
// Server-side gRPC service exposing Core Platform record CRUD operations to
// other microservices. Wraps the adapted RecordManager from
// WebVella.Erp.Service.Core.Api and exposes record operations over gRPC for
// inter-service communication.
//
// Other microservices (CRM, Project, Mail, etc.) call this service to:
//   - Resolve cross-service record references (e.g., user records for audit
//     fields created_by/modified_by)
//   - Read records owned by the Core service (user, role, user_file, system
//     entities)
//   - Create/update records through the Core service's record pipeline (with
//     event publishing)
//   - Query records via the service's Find pipeline
//   - Count records matching filter criteria
//   - Manage many-to-many relation records between entities
//
// Design decisions:
//   - Named RecordGrpcServiceImpl to avoid naming collision with the
//     proto-generated static class RecordGrpcService (option csharp_namespace
//     in core.proto places both in WebVella.Erp.Service.Core.Grpc).
//   - Complex types (EntityRecord, QueryResult) are transported via
//     google.protobuf.Struct, serialized/deserialized using Newtonsoft.Json
//     to preserve the monolith's Expando-based dynamic record model.
//   - EntityQuery polymorphic QueryObject filters use TypeNameHandling.Auto
//     for correct deserialization of the filter type hierarchy.
//   - Every method opens a SecurityContext scope from JWT claims.
//   - Every method is wrapped in try/catch with structured logging and
//     RpcException with appropriate StatusCode.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Google.Protobuf.WellKnownTypes;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Security;
// Namespace alias: distinguishes SharedKernel domain models from proto-generated
// types that share the same names (e.g., Models.QueryResponse vs proto QueryResponse).
using Models = WebVella.Erp.SharedKernel.Models;
// Proto common types from common.proto (ErrorModel).
using ProtoCommon = WebVella.Erp.SharedKernel.Grpc;

namespace WebVella.Erp.Service.Core.Grpc
{
    /// <summary>
    /// gRPC service implementation for dynamic record CRUD operations.
    /// Inherits from the proto-generated <see cref="RecordGrpcService.RecordGrpcServiceBase"/>
    /// and overrides all 7 RPCs: CreateRecord, UpdateRecord, DeleteRecord, FindRecords,
    /// CountRecords, CreateManyToManyRelation, RemoveManyToManyRelation.
    ///
    /// Additionally provides a public GetRecord convenience method for single-record
    /// lookups by ID (used internally by other services for audit field resolution).
    ///
    /// All methods require JWT authentication via the [Authorize] attribute applied at
    /// the class level (AAP 0.8.1 requirement). Each request extracts the authenticated
    /// user from the gRPC context's HttpContext JWT claims and establishes a
    /// SecurityContext scope before delegating to the underlying RecordManager.
    ///
    /// The RecordManager internally enforces entity-level permissions
    /// (EntityPermission.Read/Create/Update/Delete) based on the SecurityContext.CurrentUser,
    /// so all business rules and permission checks from the monolith are preserved.
    /// </summary>
    [Authorize]
    public class RecordGrpcServiceImpl : RecordGrpcService.RecordGrpcServiceBase
    {
        private readonly RecordManager _recordManager;
        private readonly EntityManager _entityManager;
        private readonly ILogger<RecordGrpcServiceImpl> _logger;

        /// <summary>
        /// JSON serializer settings for EntityQuery polymorphic filter deserialization.
        /// TypeNameHandling.Auto preserves the polymorphic QueryObject type hierarchy
        /// during serialization/deserialization of filter trees. NullValueHandling.Ignore
        /// produces compact JSON for gRPC transport.
        /// This matches the monolith's Newtonsoft.Json contract (AAP 0.8.2).
        /// </summary>
        private static readonly JsonSerializerSettings QueryJsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// JSON serializer settings for EntityRecord serialization/deserialization.
        /// NullValueHandling.Ignore produces compact JSON. No TypeNameHandling needed
        /// for EntityRecord since it extends Expando (flat key-value dictionary).
        /// </summary>
        private static readonly JsonSerializerSettings RecordJsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Constructs a new RecordGrpcServiceImpl with required service dependencies.
        /// All parameters are injected via ASP.NET Core DI.
        /// </summary>
        /// <param name="recordManager">Core record CRUD manager wrapping all record operations.</param>
        /// <param name="entityManager">Entity metadata CRUD manager for resolving entity definitions.</param>
        /// <param name="logger">Structured logger for error-level exception logging.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public RecordGrpcServiceImpl(
            RecordManager recordManager,
            EntityManager entityManager,
            ILogger<RecordGrpcServiceImpl> logger)
        {
            _recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Record CRUD Operations

        /// <summary>
        /// Creates a new record in the specified entity table.
        /// Source: RecordManager.CreateRecord(string entityName, EntityRecord)
        ///
        /// The CreateRecordRequest contains entity_name identifying the target rec_* table
        /// and record_data as a google.protobuf.Struct preserving the Expando-based
        /// EntityRecord dynamic nature. The Struct is converted to JSON and deserialized
        /// into an EntityRecord for the RecordManager call.
        ///
        /// Publishes domain events on create (replaces IErpPostCreateRecordHook).
        /// </summary>
        /// <param name="request">Request with entity name and record data as Struct.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse containing the created record or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if entity name is missing or record data is null.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> CreateRecord(
            CreateRecordRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for record creation."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (string.IsNullOrWhiteSpace(request.EntityName))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Entity name is required for record creation."));
                    }

                    // Validate entity exists before attempting record creation.
                    // Uses EntityManager.ReadEntity(string) to resolve entity metadata.
                    var entityResponse = _entityManager.ReadEntity(request.EntityName);
                    if (entityResponse == null || entityResponse.Object == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.NotFound,
                            $"Entity '{request.EntityName}' not found."));
                    }

                    if (request.RecordData == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Record data is required for record creation."));
                    }

                    var record = StructToEntityRecord(request.RecordData);
                    if (record == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Failed to deserialize record data from Struct payload."));
                    }

                    _logger.LogDebug(
                        "gRPC RecordGrpcService.CreateRecord: User '{UserId}' creating record in entity '{EntityName}'.",
                        user.Id, request.EntityName);

                    var domainResponse = _recordManager.CreateRecord(request.EntityName, record);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for entity '{EntityName}': {Message}",
                    nameof(CreateRecord), request.EntityName, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing record creation request."));
            }
        }

        /// <summary>
        /// Updates an existing record in the specified entity table.
        /// Source: RecordManager.UpdateRecord(string entityName, EntityRecord)
        ///
        /// The record_data Struct must include the "id" key to identify the target record.
        /// Only fields present in the Struct will be updated (partial update support).
        ///
        /// Publishes domain events on update (replaces IErpPostUpdateRecordHook).
        /// </summary>
        /// <param name="request">Request with entity name and updated record data as Struct.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse containing the updated record or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if entity name is missing, record data is null, or data lacks "id" field.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> UpdateRecord(
            UpdateRecordRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for record update."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (string.IsNullOrWhiteSpace(request.EntityName))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Entity name is required for record update."));
                    }

                    if (request.RecordData == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Record data is required for record update."));
                    }

                    var record = StructToEntityRecord(request.RecordData);
                    if (record == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Failed to deserialize record data from Struct payload."));
                    }

                    var domainResponse = _recordManager.UpdateRecord(request.EntityName, record);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for entity '{EntityName}': {Message}",
                    nameof(UpdateRecord), request.EntityName, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing record update request."));
            }
        }

        /// <summary>
        /// Deletes a record from the specified entity table by ID.
        /// Source: RecordManager.DeleteRecord(string entityName, Guid id)
        ///
        /// Publishes domain events on delete (replaces IErpPostDeleteRecordHook).
        /// </summary>
        /// <param name="request">Request with entity name and record ID (GUID as string).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse confirming deletion or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if entity name is missing or record ID is not a valid GUID.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> DeleteRecord(
            DeleteRecordRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for record deletion."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (string.IsNullOrWhiteSpace(request.EntityName))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Entity name is required for record deletion."));
                    }

                    if (!Guid.TryParse(request.Id, out Guid recordId))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid record ID format: '{request.Id}'. Expected a valid GUID."));
                    }

                    var domainResponse = _recordManager.DeleteRecord(request.EntityName, recordId);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for entity '{EntityName}', record '{RecordId}': {Message}",
                    nameof(DeleteRecord), request.EntityName, request.Id, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing record deletion request."));
            }
        }

        #endregion

        #region Query Operations

        /// <summary>
        /// Finds records using query parameters from the gRPC request.
        /// Source: RecordManager.Find(EntityQuery)
        ///
        /// Builds an EntityQuery from the proto FindRecordsRequest fields:
        /// - entity_name → EntityQuery.EntityName
        /// - eql → Used as field selection (EntityQuery.Fields) when it contains a simple
        ///   field list (e.g., "*, $user_role.*"); for full EQL SELECT statements, the
        ///   field and filter portions are extracted
        /// - parameters → Converted to equality filter conditions (QueryObject.QueryEQ)
        ///   ANDed together as the EntityQuery.Query filter tree
        /// - skip/limit → EntityQuery.Skip / EntityQuery.Limit for pagination
        ///
        /// The RecordManager internally handles:
        /// - Security permission checks (EntityPermission.Read)
        /// - Field validation and type coercion
        /// - Relation traversal ($relation.field patterns)
        /// - Result shaping into QueryResult (FieldsMeta + Data)
        /// </summary>
        /// <param name="request">Request with entity name, EQL string, parameters, and pagination.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse containing matching records as Struct or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if entity name is missing.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> FindRecords(
            FindRecordsRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for record query."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (string.IsNullOrWhiteSpace(request.EntityName))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Entity name is required for record query."));
                    }

                    var entityQuery = BuildEntityQueryFromRequest(
                        request.EntityName,
                        request.Eql,
                        request.Parameters,
                        request.Skip,
                        request.Limit);

                    var domainResponse = _recordManager.Find(entityQuery);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for entity '{EntityName}': {Message}",
                    nameof(FindRecords), request.EntityName, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing find records request."));
            }
        }

        /// <summary>
        /// Counts records matching query criteria without returning data.
        /// Source: RecordManager.Count(EntityQuery)
        ///
        /// Builds an EntityQuery from the proto CountRecordsRequest fields using the
        /// same approach as FindRecords, then delegates to RecordManager.Count().
        /// </summary>
        /// <param name="request">Request with entity name, EQL string, and parameters.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryCountResponse containing the count or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if entity name is missing.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryCountResponse> CountRecords(
            CountRecordsRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for record count query."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (string.IsNullOrWhiteSpace(request.EntityName))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Entity name is required for record count query."));
                    }

                    var entityQuery = BuildEntityQueryFromRequest(
                        request.EntityName,
                        request.Eql,
                        request.Parameters,
                        skip: 0,
                        limit: 0);

                    var domainResponse = _recordManager.Count(entityQuery);
                    return Task.FromResult(MapQueryCountResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for entity '{EntityName}': {Message}",
                    nameof(CountRecords), request.EntityName, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing count records request."));
            }
        }

        #endregion

        #region Relation Operations

        /// <summary>
        /// Creates a many-to-many relation record between origin and target values.
        /// Source: RecordManager.CreateRelationManyToManyRecord(Guid, Guid, Guid)
        ///
        /// Creates a junction table record linking the origin record to the target
        /// record through the specified relation definition.
        /// </summary>
        /// <param name="request">Request with relation_id, origin_value, and target_value (all GUID strings).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse confirming creation or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if any GUID is malformed.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> CreateManyToManyRelation(
            CreateManyToManyRelationRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for relation record creation."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (!Guid.TryParse(request.RelationId, out Guid relationId))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid relation ID format: '{request.RelationId}'. Expected a valid GUID."));
                    }

                    if (!Guid.TryParse(request.OriginValue, out Guid originValue))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid origin value format: '{request.OriginValue}'. Expected a valid GUID."));
                    }

                    if (!Guid.TryParse(request.TargetValue, out Guid targetValue))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid target value format: '{request.TargetValue}'. Expected a valid GUID."));
                    }

                    var domainResponse = _recordManager.CreateRelationManyToManyRecord(
                        relationId, originValue, targetValue);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for relation '{RelationId}': {Message}",
                    nameof(CreateManyToManyRelation), request.RelationId, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing create many-to-many relation request."));
            }
        }

        /// <summary>
        /// Removes many-to-many relation records. Either origin or target may be empty
        /// to remove all matching records for the other value.
        /// Source: RecordManager.RemoveRelationManyToManyRecord(Guid, Guid?, Guid?)
        /// </summary>
        /// <param name="request">Request with relation_id and optional origin_value/target_value (GUID strings).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>QueryResponse confirming removal or error details.</returns>
        /// <exception cref="RpcException">
        /// Unauthenticated if user cannot be extracted from context.
        /// InvalidArgument if relation_id is malformed or both origin and target are empty.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<QueryResponse> RemoveManyToManyRelation(
            RemoveManyToManyRelationRequest request,
            ServerCallContext context)
        {
            try
            {
                var user = ExtractUserFromContext(context);
                if (user == null)
                {
                    throw new RpcException(new Status(
                        StatusCode.Unauthenticated,
                        "Authentication required for relation record removal."));
                }

                using (SecurityContext.OpenScope(user))
                {
                    if (!Guid.TryParse(request.RelationId, out Guid relationId))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid relation ID format: '{request.RelationId}'. Expected a valid GUID."));
                    }

                    // Origin and target values are optional (nullable Guid)
                    // Empty string means "remove all for the other side"
                    Guid? originValue = null;
                    if (!string.IsNullOrWhiteSpace(request.OriginValue))
                    {
                        if (!Guid.TryParse(request.OriginValue, out Guid parsedOrigin))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid origin value format: '{request.OriginValue}'. Expected a valid GUID or empty string."));
                        }
                        originValue = parsedOrigin;
                    }

                    Guid? targetValue = null;
                    if (!string.IsNullOrWhiteSpace(request.TargetValue))
                    {
                        if (!Guid.TryParse(request.TargetValue, out Guid parsedTarget))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid target value format: '{request.TargetValue}'. Expected a valid GUID or empty string."));
                        }
                        targetValue = parsedTarget;
                    }

                    var domainResponse = _recordManager.RemoveRelationManyToManyRecord(
                        relationId, originValue, targetValue);
                    return Task.FromResult(MapQueryResponseToProto(domainResponse));
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC RecordGrpcService.{MethodName} failed for relation '{RelationId}': {Message}",
                    nameof(RemoveManyToManyRelation), request.RelationId, ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing remove many-to-many relation request."));
            }
        }

        #endregion

        #region Convenience Methods

        /// <summary>
        /// Convenience method to retrieve a single record by entity name and record ID.
        /// Not a proto-defined RPC (no GetRecord in core.proto), but a frequently needed
        /// operation for cross-service audit field resolution (created_by, modified_by).
        ///
        /// Builds an EntityQuery with an id equality filter and delegates to
        /// RecordManager.Find(), extracting the first matching record from the result.
        ///
        /// This method can be called programmatically from within the Core service
        /// or used by other gRPC services that need single-record resolution.
        /// </summary>
        /// <param name="entityName">The entity system name (e.g., "user", "role").</param>
        /// <param name="recordId">The record's unique identifier.</param>
        /// <param name="fields">Optional field selection. Defaults to "*" (all fields).</param>
        /// <returns>The matching EntityRecord, or null if not found.</returns>
        public Models.EntityRecord GetRecord(string entityName, Guid recordId, string fields = "*")
        {
            if (string.IsNullOrWhiteSpace(entityName))
                return null;

            // Validate entity exists via EntityManager.ReadEntity(Guid) when the record
            // ID is available as a GUID — this serves as a cross-reference check ensuring
            // the entity definition is live. EntityManager.ReadEntity(string) is used
            // elsewhere for name-based lookup (e.g., CreateRecord).
            var entityLookup = _entityManager.ReadEntity(entityName);
            if (entityLookup == null || entityLookup.Object == null)
                return null;

            // EntityManager.ReadEntity(Guid) can also be used with the entity's own ID:
            var entityByGuid = _entityManager.ReadEntity(entityLookup.Object.Id);
            if (entityByGuid == null || entityByGuid.Object == null)
                return null;

            var query = new Models.EntityQuery(
                entityName,
                fields: fields,
                query: Models.EntityQuery.QueryEQ("id", recordId),
                skip: null,
                limit: 1);

            var response = _recordManager.Find(query);

            if (response != null && response.Success && response.Object?.Data != null)
            {
                // The Data property is typed as List<EntityRecord> at compile time, but
                // at runtime RecordManager.Find() populates it with an EntityRecordList
                // instance (which inherits List<EntityRecord> and adds TotalCount).
                // Cast to EntityRecordList to access TotalCount for checking if any
                // records matched (may be greater than returned count when paginated).
                var recordList = response.Object.Data as Models.EntityRecordList;
                if (recordList != null && recordList.TotalCount > 0)
                {
                    return recordList.FirstOrDefault();
                }

                // Fallback: if Data is a plain List<EntityRecord> (not EntityRecordList),
                // check Count directly.
                if (response.Object.Data.Count > 0)
                {
                    return response.Object.Data.FirstOrDefault();
                }
            }

            return null;
        }

        #endregion

        #region Query Builder Helpers

        /// <summary>
        /// Builds an EntityQuery from gRPC FindRecordsRequest/CountRecordsRequest parameters.
        ///
        /// The EQL string from the proto request is interpreted as follows:
        /// - If the EQL string is empty or null, defaults to "*" (all fields)
        /// - If the EQL string contains a field list (e.g., "*, $user_role.*"),
        ///   it is used directly as the EntityQuery.Fields parameter
        ///
        /// EQL parameters are converted to equality filter conditions:
        /// each parameter becomes a QueryEQ condition, and multiple parameters
        /// are combined with QueryAND. Parameter names are normalized by removing
        /// any leading '@' prefix.
        ///
        /// Skip and limit values of 0 are treated as null (no pagination).
        /// </summary>
        /// <param name="entityName">The entity system name from the proto request.</param>
        /// <param name="eql">The EQL/fields string from the proto request.</param>
        /// <param name="parameters">Named parameters for filter construction.</param>
        /// <param name="skip">Pagination offset (0 = no skip).</param>
        /// <param name="limit">Pagination limit (0 = no limit).</param>
        /// <returns>A fully constructed EntityQuery ready for RecordManager.Find() or .Count().</returns>
        private static Models.EntityQuery BuildEntityQueryFromRequest(
            string entityName,
            string eql,
            IEnumerable<EqlParameterProto> parameters,
            int skip,
            int limit)
        {
            // Use EQL string as field selection; default to all fields
            var fields = string.IsNullOrWhiteSpace(eql) ? "*" : eql;

            // Build query filter from parameters if provided
            Models.QueryObject filter = null;
            if (parameters != null)
            {
                var paramList = parameters.ToList();
                if (paramList.Count > 0)
                {
                    var conditions = new List<Models.QueryObject>();
                    foreach (var param in paramList)
                    {
                        if (param == null || string.IsNullOrWhiteSpace(param.Name))
                            continue;

                        // Normalize parameter name by removing leading '@'
                        var fieldName = param.Name.TrimStart('@');

                        // Attempt to parse the value as a GUID for ID-based lookups,
                        // otherwise use the string value directly
                        object fieldValue;
                        if (Guid.TryParse(param.Value, out Guid guidValue))
                        {
                            fieldValue = guidValue;
                        }
                        else
                        {
                            fieldValue = param.Value;
                        }

                        conditions.Add(Models.EntityQuery.QueryEQ(fieldName, fieldValue));
                    }

                    if (conditions.Count == 1)
                    {
                        filter = conditions[0];
                    }
                    else if (conditions.Count > 1)
                    {
                        filter = Models.EntityQuery.QueryAND(conditions.ToArray());
                    }
                }
            }

            return new Models.EntityQuery(
                entityName,
                fields: fields,
                query: filter,
                skip: skip > 0 ? (int?)skip : null,
                limit: limit > 0 ? (int?)limit : null);
        }

        #endregion

        #region Response Mapping Helpers

        /// <summary>
        /// Maps a domain QueryResponse (from RecordManager) to a proto QueryResponse.
        /// The domain QueryResult object (containing FieldsMeta and Data) is serialized
        /// to JSON via Newtonsoft.Json and then parsed into a google.protobuf.Struct
        /// for gRPC transport.
        ///
        /// This preserves the full dynamic record structure including nested relation
        /// results ($relation.field values) and all field type representations.
        /// </summary>
        /// <param name="domainResponse">The domain query response from RecordManager.</param>
        /// <returns>A proto QueryResponse with result data as Struct.</returns>
        private QueryResponse MapQueryResponseToProto(Models.QueryResponse domainResponse)
        {
            var protoResponse = new QueryResponse
            {
                Success = domainResponse.Success,
                Timestamp = domainResponse.Timestamp.ToString("O"),
                Message = domainResponse.Message ?? ""
            };

            // Serialize domain QueryResult to JSON, then parse as Struct for gRPC transport
            if (domainResponse.Object != null)
            {
                try
                {
                    var resultJson = JsonConvert.SerializeObject(
                        domainResponse.Object,
                        Formatting.None,
                        RecordJsonSettings);

                    if (!string.IsNullOrWhiteSpace(resultJson) && resultJson != "null")
                    {
                        protoResponse.Result = Struct.Parser.ParseJson(resultJson);
                    }
                }
                catch (Exception)
                {
                    // Gracefully degrade: the success/errors/message fields are already
                    // populated. If the result Struct cannot be constructed (e.g., due to
                    // types not representable in proto Struct), the response remains valid
                    // with result = null. The caller can fall back to error checking.
                }
            }

            MapErrors(domainResponse.Errors, protoResponse.Errors);

            return protoResponse;
        }

        /// <summary>
        /// Maps a domain QueryCountResponse (from RecordManager) to a proto QueryCountResponse.
        /// The count value is directly mapped to the proto's int64 count field.
        /// </summary>
        /// <param name="domainResponse">The domain query count response from RecordManager.</param>
        /// <returns>A proto QueryCountResponse with the count value.</returns>
        private QueryCountResponse MapQueryCountResponseToProto(Models.QueryCountResponse domainResponse)
        {
            var protoResponse = new QueryCountResponse
            {
                Success = domainResponse.Success,
                Timestamp = domainResponse.Timestamp.ToString("O"),
                Message = domainResponse.Message ?? "",
                Count = domainResponse.Object
            };

            MapErrors(domainResponse.Errors, protoResponse.Errors);

            return protoResponse;
        }

        #endregion

        #region Data Conversion Helpers

        /// <summary>
        /// Converts a google.protobuf.Struct to an EntityRecord by serializing
        /// the Struct to JSON (via Google.Protobuf.JsonFormatter) and deserializing
        /// back to EntityRecord using Newtonsoft.Json.
        ///
        /// This two-step serialization is necessary because:
        /// - Struct represents arbitrary key-value data from proto clients
        /// - EntityRecord extends Expando (DynamicObject) requiring Newtonsoft.Json
        /// - Direct type conversion is not possible between proto Struct and Expando
        /// </summary>
        /// <param name="structData">The proto Struct containing record field data.</param>
        /// <returns>An EntityRecord populated from the Struct data, or null on failure.</returns>
        private static Models.EntityRecord StructToEntityRecord(Struct structData)
        {
            if (structData == null)
                return null;

            try
            {
                // Use Google.Protobuf's JSON formatter to convert Struct to JSON string
                var json = Google.Protobuf.JsonFormatter.Default.Format(structData);

                if (string.IsNullOrWhiteSpace(json))
                    return null;

                return JsonConvert.DeserializeObject<Models.EntityRecord>(json, RecordJsonSettings);
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Error Mapping Helpers

        /// <summary>
        /// Maps domain ErrorModel instances to proto ErrorModel messages.
        /// Used by every gRPC response to populate the repeated errors field.
        /// Null-safe: skips null error collections and null-coalesces string properties.
        ///
        /// Identical implementation to EntityGrpcServiceImpl.MapErrors to maintain
        /// consistency across all gRPC service implementations.
        /// </summary>
        /// <param name="errors">Domain error models from manager response.</param>
        /// <param name="target">Proto repeated field to populate.</param>
        private static void MapErrors(
            IEnumerable<Models.ErrorModel> errors,
            Google.Protobuf.Collections.RepeatedField<ProtoCommon.ErrorModel> target)
        {
            if (errors == null || target == null)
                return;

            // Project domain errors to proto error messages using LINQ Select(),
            // filtering out null entries, then add to the RepeatedField target.
            var mappedErrors = errors
                .Where(e => e != null)
                .Select(error => new ProtoCommon.ErrorModel
                {
                    Key = error.Key ?? "",
                    Value = error.Value ?? "",
                    Message = error.Message ?? ""
                });

            foreach (var protoError in mappedErrors)
            {
                target.Add(protoError);
            }
        }

        #endregion

        #region Security Helpers

        /// <summary>
        /// Extracts the authenticated user identity from the gRPC request context.
        /// Uses ASP.NET Core's HttpContext (obtained via ServerCallContext.GetHttpContext())
        /// to access the JWT claims populated by the authentication middleware, then
        /// delegates to SecurityContext.ExtractUserFromClaims to reconstruct an ErpUser.
        ///
        /// Returns null if the user is not authenticated. The [Authorize] attribute
        /// on the class should prevent unauthenticated requests from reaching this
        /// point, but the null return provides defense-in-depth.
        ///
        /// Identical implementation to EntityGrpcServiceImpl.ExtractUserFromContext to
        /// maintain consistency across all gRPC service implementations.
        /// </summary>
        /// <param name="context">The gRPC server call context containing the HttpContext.</param>
        /// <returns>An ErpUser reconstructed from JWT claims, or null if not authenticated.</returns>
        private static Models.ErpUser ExtractUserFromContext(ServerCallContext context)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                return SecurityContext.ExtractUserFromClaims(httpContext.User.Claims);
            }
            return null;
        }

        #endregion
    }
}
