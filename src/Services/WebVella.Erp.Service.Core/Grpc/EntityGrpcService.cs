// =============================================================================
// WebVella ERP — Core Platform Microservice
// EntityGrpcServiceImpl: gRPC service for entity metadata operations
// =============================================================================
// Server-side gRPC service exposing Core Platform entity, field, and relation
// metadata to other microservices. Wraps EntityManager and EntityRelationManager
// read operations for inter-service communication via protocol buffers.
//
// Other microservices (CRM, Project, Mail, etc.) call this service to:
//   - Look up entity definitions (metadata, fields, relations)
//   - Resolve field definitions for cross-service EQL queries
//   - Get relation metadata for API composition patterns
//
// Design decisions:
//   - Named EntityGrpcServiceImpl to avoid naming collision with the
//     proto-generated static class EntityGrpcService (option csharp_namespace
//     in core.proto places both in WebVella.Erp.Service.Core.Grpc).
//   - Implements only read RPCs (ReadEntity, ReadEntities, ReadFields,
//     ReadRelation, ReadRelations). Write RPCs default to Unimplemented.
//   - Complex types (Entity, Field, EntityRelation) are mapped to structured
//     proto messages. Polymorphic field options use google.protobuf.Struct
//     serialized via Newtonsoft.Json with TypeNameHandling.Auto.
//   - Every method opens a SecurityContext scope from JWT claims.
//   - Every method is wrapped in try/catch with structured logging and
//     RpcException with appropriate StatusCode.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Google.Protobuf.WellKnownTypes;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Security;
// Namespace alias: distinguishes SharedKernel domain models from proto-generated
// types that share the same names (e.g., Models.EntityResponse vs proto EntityResponse).
using Models = WebVella.Erp.SharedKernel.Models;
// Proto common types from common.proto (ErrorModel).
using ProtoCommon = WebVella.Erp.SharedKernel.Grpc;

namespace WebVella.Erp.Service.Core.Grpc
{
    /// <summary>
    /// gRPC service implementation for entity metadata read operations.
    /// Inherits from the proto-generated <see cref="EntityGrpcService.EntityGrpcServiceBase"/>
    /// and overrides read RPCs: ReadEntity, ReadEntities, ReadFields, ReadRelation, ReadRelations.
    ///
    /// All methods require JWT authentication via the [Authorize] attribute applied at
    /// the class level (AAP 0.8.1 requirement). Each request extracts the authenticated
    /// user from the gRPC context's HttpContext JWT claims and establishes a
    /// SecurityContext scope before delegating to the underlying manager.
    ///
    /// Responses map domain model types to proto message types, preserving the
    /// polymorphic field type hierarchy through JSON-serialized options Struct.
    /// </summary>
    [Authorize]
    public class EntityGrpcServiceImpl : EntityGrpcService.EntityGrpcServiceBase
    {
        private readonly EntityManager _entityManager;
        private readonly EntityRelationManager _relationManager;
        private readonly ILogger<EntityGrpcServiceImpl> _logger;

        /// <summary>
        /// JSON serializer settings for field type-specific option serialization.
        /// TypeNameHandling.Auto preserves the polymorphic Field type hierarchy
        /// (21 concrete types: TextField, NumberField, GuidField, etc.) during
        /// serialization to the FieldProto.Options google.protobuf.Struct field.
        /// This matches the monolith's Newtonsoft.Json contract (AAP 0.8.2).
        /// </summary>
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };

        /// <summary>
        /// Common field property names that are mapped directly to FieldProto's
        /// top-level proto fields. These are excluded from the options Struct to
        /// avoid duplication: the receiver can read common properties from proto
        /// fields and type-specific properties from the options Struct.
        /// </summary>
        private static readonly HashSet<string> CommonFieldPropertyNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "id", "name", "label", "placeholderText", "description", "helpText",
                "required", "unique", "searchable", "auditable", "system",
                "permissions", "enableSecurity", "fieldType", "entityName",
                "$type" // TypeNameHandling.Auto metadata key
            };

        /// <summary>
        /// Constructs a new EntityGrpcServiceImpl with required service dependencies.
        /// All parameters are injected via ASP.NET Core DI.
        /// </summary>
        /// <param name="entityManager">Core entity metadata CRUD manager.</param>
        /// <param name="relationManager">Entity relation metadata CRUD manager.</param>
        /// <param name="logger">Structured logger for error-level exception logging.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public EntityGrpcServiceImpl(
            EntityManager entityManager,
            EntityRelationManager relationManager,
            ILogger<EntityGrpcServiceImpl> logger)
        {
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            _relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Entity Operations

        /// <summary>
        /// Reads a single entity by ID or name.
        /// Source: EntityManager.ReadEntity(Guid id) / EntityManager.ReadEntity(string name)
        ///
        /// The ReadEntityRequest contains both an 'id' and 'name' field; the ID takes
        /// precedence if both are set. Returns an EntityResponse proto message with
        /// the full entity definition (including fields) mapped from the domain model.
        /// </summary>
        /// <param name="request">Request containing entity ID (GUID string) or name.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>EntityResponse containing the entity proto or error details.</returns>
        /// <exception cref="RpcException">
        /// InvalidArgument if neither ID nor name is provided.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<EntityResponse> ReadEntity(
            ReadEntityRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    Models.EntityResponse domainResponse;

                    // ID takes precedence over name when both are provided
                    if (!string.IsNullOrWhiteSpace(request.Id))
                    {
                        if (!Guid.TryParse(request.Id, out Guid entityId))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid entity ID format: '{request.Id}'. Expected a valid GUID."));
                        }
                        domainResponse = _entityManager.ReadEntity(entityId);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.Name))
                    {
                        domainResponse = _entityManager.ReadEntity(request.Name);
                    }
                    else
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Either 'id' or 'name' must be provided in the ReadEntityRequest."));
                    }

                    var reply = new EntityResponse
                    {
                        Success = domainResponse.Success,
                        Timestamp = domainResponse.Timestamp.ToString("O"),
                        Message = domainResponse.Message ?? "",
                        Hash = domainResponse.Hash ?? ""
                    };

                    if (domainResponse.Object != null)
                    {
                        reply.Entity = MapEntityToProto(domainResponse.Object);
                    }

                    MapErrors(domainResponse.Errors, reply.Errors);

                    return Task.FromResult(reply);
                }
            }
            catch (RpcException)
            {
                // Re-throw RpcExceptions as-is (already have proper status codes)
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC EntityGrpcService.{MethodName} failed: {Message}",
                    nameof(ReadEntity), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing entity read request."));
            }
        }

        /// <summary>
        /// Reads all entities in the system (cache-aware).
        /// Source: EntityManager.ReadEntities()
        ///
        /// Returns the complete entity catalog with all field definitions.
        /// Used by other microservices to discover entity schemas at startup
        /// or when resolving cross-service EQL queries.
        /// </summary>
        /// <param name="request">Empty request (no parameters required).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>EntityListResponse containing all entity protos or error details.</returns>
        public override Task<EntityListResponse> ReadEntities(
            ReadEntitiesRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    var domainResponse = _entityManager.ReadEntities();

                    var reply = new EntityListResponse
                    {
                        Success = domainResponse.Success,
                        Timestamp = domainResponse.Timestamp.ToString("O"),
                        Message = domainResponse.Message ?? "",
                        Hash = domainResponse.Hash ?? ""
                    };

                    if (domainResponse.Object != null)
                    {
                        foreach (var entity in domainResponse.Object)
                        {
                            var proto = MapEntityToProto(entity);
                            if (proto != null)
                            {
                                reply.Entities.Add(proto);
                            }
                        }
                    }

                    MapErrors(domainResponse.Errors, reply.Errors);

                    return Task.FromResult(reply);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC EntityGrpcService.{MethodName} failed: {Message}",
                    nameof(ReadEntities), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing entities read request."));
            }
        }

        #endregion

        #region Field Operations

        /// <summary>
        /// Reads fields for a specific entity or all fields across all entities.
        /// Source: EntityManager.ReadFields(Guid entityId) / EntityManager.ReadFields()
        ///
        /// When entity_id is provided and valid, returns fields for that entity only.
        /// When entity_id is empty, returns all fields from all entities.
        /// Polymorphic field types are preserved via the FieldProto.Options Struct.
        /// </summary>
        /// <param name="request">Request with optional entity_id (GUID string).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>FieldListResponse containing field protos or error details.</returns>
        public override Task<FieldListResponse> ReadFields(
            ReadFieldsRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    Models.FieldListResponse domainResponse;

                    if (!string.IsNullOrWhiteSpace(request.EntityId))
                    {
                        if (!Guid.TryParse(request.EntityId, out Guid entityId))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid entity ID format: '{request.EntityId}'. Expected a valid GUID."));
                        }
                        domainResponse = _entityManager.ReadFields(entityId);
                    }
                    else
                    {
                        // Read all fields from all entities when no entity_id specified
                        domainResponse = _entityManager.ReadFields();
                    }

                    var reply = new FieldListResponse
                    {
                        Success = domainResponse.Success,
                        Timestamp = domainResponse.Timestamp.ToString("O"),
                        Message = domainResponse.Message ?? ""
                    };

                    if (domainResponse.Object?.Fields != null)
                    {
                        foreach (var field in domainResponse.Object.Fields)
                        {
                            var proto = MapFieldToProto(field);
                            if (proto != null)
                            {
                                reply.Fields.Add(proto);
                            }
                        }
                    }

                    MapErrors(domainResponse.Errors, reply.Errors);

                    return Task.FromResult(reply);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC EntityGrpcService.{MethodName} failed: {Message}",
                    nameof(ReadFields), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing fields read request."));
            }
        }

        #endregion

        #region Relation Operations

        /// <summary>
        /// Reads a single entity relation by ID or name.
        /// Source: EntityRelationManager.Read(Guid id) / EntityRelationManager.Read(string name)
        ///
        /// The ReadRelationRequest contains both an 'id' and 'name' field; the ID
        /// takes precedence if both are set. Used by other services to resolve
        /// relation metadata for cross-service query composition.
        /// </summary>
        /// <param name="request">Request containing relation ID (GUID string) or name.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>EntityRelationResponse containing the relation proto or error details.</returns>
        /// <exception cref="RpcException">
        /// InvalidArgument if neither ID nor name is provided.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<EntityRelationResponse> ReadRelation(
            ReadRelationRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    Models.EntityRelationResponse domainResponse;

                    // ID takes precedence over name when both are provided
                    if (!string.IsNullOrWhiteSpace(request.Id))
                    {
                        if (!Guid.TryParse(request.Id, out Guid relationId))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid relation ID format: '{request.Id}'. Expected a valid GUID."));
                        }
                        domainResponse = _relationManager.Read(relationId);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.Name))
                    {
                        domainResponse = _relationManager.Read(request.Name);
                    }
                    else
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Either 'id' or 'name' must be provided in the ReadRelationRequest."));
                    }

                    var reply = new EntityRelationResponse
                    {
                        Success = domainResponse.Success,
                        Timestamp = domainResponse.Timestamp.ToString("O"),
                        Message = domainResponse.Message ?? ""
                    };

                    if (domainResponse.Object != null)
                    {
                        reply.Relation = MapRelationToProto(domainResponse.Object);
                    }

                    MapErrors(domainResponse.Errors, reply.Errors);

                    return Task.FromResult(reply);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC EntityGrpcService.{MethodName} failed: {Message}",
                    nameof(ReadRelation), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing relation read request."));
            }
        }

        /// <summary>
        /// Reads all entity relations in the system.
        /// Source: EntityRelationManager.Read()
        ///
        /// Returns the complete relation catalog. Used by other microservices to
        /// discover cross-entity relationship definitions for EQL query composition
        /// and API-based relation traversal.
        /// </summary>
        /// <param name="request">Empty request (no parameters required).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>EntityRelationListResponse containing all relation protos or error details.</returns>
        public override Task<EntityRelationListResponse> ReadRelations(
            ReadRelationsRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    var domainResponse = _relationManager.Read();

                    var reply = new EntityRelationListResponse
                    {
                        Success = domainResponse.Success,
                        Timestamp = domainResponse.Timestamp.ToString("O"),
                        Message = domainResponse.Message ?? ""
                    };

                    if (domainResponse.Object != null)
                    {
                        foreach (var relation in domainResponse.Object)
                        {
                            var proto = MapRelationToProto(relation);
                            if (proto != null)
                            {
                                reply.Relations.Add(proto);
                            }
                        }
                    }

                    MapErrors(domainResponse.Errors, reply.Errors);

                    return Task.FromResult(reply);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC EntityGrpcService.{MethodName} failed: {Message}",
                    nameof(ReadRelations), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing relations read request."));
            }
        }

        #endregion

        #region Mapping Helpers — Entity

        /// <summary>
        /// Maps a SharedKernel Entity domain model to a proto EntityProto message.
        /// Converts all properties including the polymorphic Fields collection
        /// and role-based RecordPermissions.
        /// </summary>
        /// <param name="entity">The domain entity to convert.</param>
        /// <returns>A fully populated EntityProto, or null if the input is null.</returns>
        private static EntityProto MapEntityToProto(Models.Entity entity)
        {
            if (entity == null)
                return null;

            var proto = new EntityProto
            {
                Id = entity.Id.ToString(),
                Name = entity.Name ?? "",
                Label = entity.Label ?? "",
                LabelPlural = entity.LabelPlural ?? "",
                System = entity.System,
                IconName = entity.IconName ?? "",
                Color = entity.Color ?? "",
                RecordScreenIdField = entity.RecordScreenIdField?.ToString() ?? "",
                Hash = entity.Hash ?? ""
            };

            if (entity.RecordPermissions != null)
            {
                proto.RecordPermissions = MapPermissionsToProto(entity.RecordPermissions);
            }

            if (entity.Fields != null)
            {
                foreach (var field in entity.Fields)
                {
                    var fieldProto = MapFieldToProto(field);
                    if (fieldProto != null)
                    {
                        proto.Fields.Add(fieldProto);
                    }
                }
            }

            return proto;
        }

        #endregion

        #region Mapping Helpers — Field

        /// <summary>
        /// Maps a SharedKernel Field domain model to a proto FieldProto message.
        /// Common properties (id, name, label, required, etc.) are set directly
        /// on the FieldProto. Type-specific properties (e.g., DefaultValue, MinValue,
        /// MaxLength for TextField, NumberField, etc.) are serialized to JSON and
        /// stored in the FieldProto.Options google.protobuf.Struct field.
        ///
        /// This approach preserves the polymorphic 21-field-type hierarchy without
        /// requiring all field types to be defined in proto. The receiving service
        /// deserializes the Options Struct back to JSON and combines it with the
        /// common properties to reconstruct the full Field object.
        /// </summary>
        /// <param name="field">The domain field to convert (any concrete Field subclass).</param>
        /// <returns>A fully populated FieldProto, or null if the input is null.</returns>
        private static FieldProto MapFieldToProto(Models.Field field)
        {
            if (field == null)
                return null;

            var proto = new FieldProto
            {
                Id = field.Id.ToString(),
                Name = field.Name ?? "",
                Label = field.Label ?? "",
                // Derive field type from the concrete class name (e.g., "TextField", "NumberField")
                FieldType = field.GetType().Name,
                Required = field.Required,
                Unique = field.Unique,
                Searchable = field.Searchable,
                Auditable = field.Auditable,
                System = field.System,
                Description = field.Description ?? "",
                HelpText = field.HelpText ?? "",
                EnableSecurity = field.EnableSecurity
            };

            // Map field-level permissions (CanRead, CanUpdate only — no CanCreate/CanDelete on fields)
            if (field.Permissions != null)
            {
                proto.Permissions = new RecordPermissionsProto();
                if (field.Permissions.CanRead != null)
                {
                    proto.Permissions.CanRead.AddRange(
                        field.Permissions.CanRead.Select(g => g.ToString()));
                }
                if (field.Permissions.CanUpdate != null)
                {
                    proto.Permissions.CanUpdate.AddRange(
                        field.Permissions.CanUpdate.Select(g => g.ToString()));
                }
            }

            // Serialize type-specific properties to the options Struct.
            // Common properties are excluded to avoid duplication.
            try
            {
                var json = JsonConvert.SerializeObject(field, JsonSettings);
                var jObj = JObject.Parse(json);

                // Remove properties already mapped to FieldProto top-level fields
                var propsToRemove = jObj.Properties()
                    .Where(p => CommonFieldPropertyNames.Contains(p.Name))
                    .Select(p => p.Name)
                    .ToList();

                foreach (var propName in propsToRemove)
                {
                    jObj.Remove(propName);
                }

                // Only create Struct if there are remaining type-specific properties
                if (jObj.HasValues)
                {
                    var optionsJson = jObj.ToString(Formatting.None);
                    proto.Options = Struct.Parser.ParseJson(optionsJson);
                }
            }
            catch (Exception)
            {
                // Gracefully degrade: common field properties are already mapped
                // to FieldProto fields. Options are supplementary for type-specific
                // data. Serialization failure should not break the field metadata.
            }

            return proto;
        }

        #endregion

        #region Mapping Helpers — Relation

        /// <summary>
        /// Maps a SharedKernel EntityRelation domain model to a proto EntityRelationProto message.
        /// Converts all properties including resolved entity/field names and relation type.
        /// </summary>
        /// <param name="relation">The domain entity relation to convert.</param>
        /// <returns>A fully populated EntityRelationProto, or null if the input is null.</returns>
        private static EntityRelationProto MapRelationToProto(Models.EntityRelation relation)
        {
            if (relation == null)
                return null;

            return new EntityRelationProto
            {
                Id = relation.Id.ToString(),
                Name = relation.Name ?? "",
                Label = relation.Label ?? "",
                // Convert EntityRelationType enum to string ("OneToOne", "OneToMany", "ManyToMany")
                RelationType = relation.RelationType.ToString(),
                OriginEntityId = relation.OriginEntityId.ToString(),
                OriginFieldId = relation.OriginFieldId.ToString(),
                TargetEntityId = relation.TargetEntityId.ToString(),
                TargetFieldId = relation.TargetFieldId.ToString(),
                System = relation.System,
                Description = relation.Description ?? "",
                OriginEntityName = relation.OriginEntityName ?? "",
                OriginFieldName = relation.OriginFieldName ?? "",
                TargetEntityName = relation.TargetEntityName ?? "",
                TargetFieldName = relation.TargetFieldName ?? ""
            };
        }

        #endregion

        #region Mapping Helpers — Permissions

        /// <summary>
        /// Maps domain RecordPermissions to proto RecordPermissionsProto.
        /// Converts each Guid list to a repeated string field of GUID strings.
        /// </summary>
        /// <param name="permissions">The domain record permissions to convert.</param>
        /// <returns>A RecordPermissionsProto, or null if the input is null.</returns>
        private static RecordPermissionsProto MapPermissionsToProto(Models.RecordPermissions permissions)
        {
            if (permissions == null)
                return null;

            var proto = new RecordPermissionsProto();

            if (permissions.CanRead != null)
                proto.CanRead.AddRange(permissions.CanRead.Select(g => g.ToString()));
            if (permissions.CanCreate != null)
                proto.CanCreate.AddRange(permissions.CanCreate.Select(g => g.ToString()));
            if (permissions.CanUpdate != null)
                proto.CanUpdate.AddRange(permissions.CanUpdate.Select(g => g.ToString()));
            if (permissions.CanDelete != null)
                proto.CanDelete.AddRange(permissions.CanDelete.Select(g => g.ToString()));

            return proto;
        }

        #endregion

        #region Mapping Helpers — Errors

        /// <summary>
        /// Maps domain ErrorModel instances to proto ErrorModel messages.
        /// Used by every gRPC response to populate the repeated errors field.
        /// Null-safe: skips null error collections and null-coalesces string properties.
        /// </summary>
        /// <param name="errors">Domain error models from manager response.</param>
        /// <param name="target">Proto repeated field to populate.</param>
        private static void MapErrors(
            IEnumerable<Models.ErrorModel> errors,
            Google.Protobuf.Collections.RepeatedField<ProtoCommon.ErrorModel> target)
        {
            if (errors == null || target == null)
                return;

            foreach (var error in errors)
            {
                if (error == null)
                    continue;

                target.Add(new ProtoCommon.ErrorModel
                {
                    Key = error.Key ?? "",
                    Value = error.Value ?? "",
                    Message = error.Message ?? ""
                });
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
