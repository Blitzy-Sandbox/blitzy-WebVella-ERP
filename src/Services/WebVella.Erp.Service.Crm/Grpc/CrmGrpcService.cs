// =============================================================================
// CrmGrpcService.cs — Server-Side gRPC Service for CRM Microservice
// =============================================================================
// Implements the gRPC service for the CRM microservice, enabling other
// microservices (Project, Mail, Core, Gateway) to resolve CRM entities
// (accounts, contacts, cases, addresses, salutations) across service boundaries.
//
// In the monolith, these entities were accessed via direct in-process method
// calls on RecordManager and SQL joins across shared rec_* tables. This gRPC
// service replaces those direct database lookups with a well-defined API contract.
//
// Proto source: proto/crm.proto (CrmService definition)
// Pattern template: Core gRPC services (EntityGrpcService, RecordGrpcService)
//
// Cross-service reference analysis (AAP 0.7.1):
//   - Account → Project: resolve via CRM gRPC call on read
//   - Contact → Email: Mail service stores contact UUID, resolves via gRPC
//   - Case → Task: denormalized case_id in Project DB, CRM publishes events
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
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Crm.Controllers;

// Alias to disambiguate proto-generated ErrorModel from SharedKernel ErrorModel
using GrpcErrorModel = WebVella.Erp.SharedKernel.Grpc.ErrorModel;

namespace WebVella.Erp.Service.Crm.Grpc
{
	/// <summary>
	/// gRPC service implementation for the CRM microservice providing inter-service
	/// entity resolution for accounts, contacts, and cases. Inherits from the
	/// proto-generated <see cref="CrmService.CrmServiceBase"/> to implement the
	/// CRM service RPC contract.
	///
	/// Design decisions (AAP-derived):
	/// <list type="bullet">
	///   <item>JSON string transport for complex ERP types (EntityRecord with dynamic
	///     Expando base) in bulk methods — matching Core gRPC pattern</item>
	///   <item>SecurityContext.OpenScope on every method — preserving monolith
	///     per-request authentication semantics</item>
	///   <item>Entity boundary validation — CRM gRPC only serves its 8 owned entities</item>
	///   <item>Bulk resolution via ResolveCrmEntities to minimize N+1 gRPC calls</item>
	/// </list>
	///
	/// Rules (AAP 0.8.1, 0.8.2):
	/// <list type="bullet">
	///   <item>[Authorize] on class by default per AAP 0.8.2 code quality</item>
	///   <item>Newtonsoft.Json for API contract stability per AAP 0.8.2</item>
	///   <item>No direct database access — delegates to service-scoped managers</item>
	///   <item>Stateless service — no singletons, no static mutable state</item>
	/// </list>
	/// </summary>
	[Authorize]
	public class CrmGrpcService : CrmService.CrmServiceBase
	{
		#region ===== Static Configuration =====

		/// <summary>
		/// Set of valid CRM-owned entity names for service boundary enforcement.
		/// Queries targeting entities outside this set are rejected with InvalidArgument.
		/// Entity names match CrmEntityConstants (Domain/Entities).
		/// </summary>
		private static readonly HashSet<string> CrmEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"account",
			"contact",
			"case",
			"address",
			"salutation",
			"case_status",
			"case_type",
			"industry"
		};

		/// <summary>
		/// Shared JSON serializer settings used for EntityRecord serialization.
		/// NullValueHandling.Ignore reduces payload size by omitting null fields.
		/// </summary>
		private static readonly JsonSerializerSettings SerializationSettings = new JsonSerializerSettings
		{
			NullValueHandling = NullValueHandling.Ignore
		};

		/// <summary>
		/// JSON deserializer settings for polymorphic EntityQuery/QueryObject deserialization.
		/// TypeNameHandling.Auto enables correct deserialization of QueryObject subtype hierarchy.
		/// </summary>
		private static readonly JsonSerializerSettings DeserializationSettings = new JsonSerializerSettings
		{
			TypeNameHandling = TypeNameHandling.Auto
		};

		#endregion

		#region ===== Private Fields =====

		private readonly ICrmRecordOperations _recordManager;
		private readonly ICrmEntityOperations _entityManager;
		private readonly ICrmRelationOperations _entityRelationManager;
		private readonly ILogger<CrmGrpcService> _logger;

		#endregion

		#region ===== Constructor =====

		/// <summary>
		/// Constructs a CrmGrpcService with all required dependencies injected via DI.
		/// Uses the CRM service's own abstraction interfaces (ICrmRecordOperations,
		/// ICrmEntityOperations, ICrmRelationOperations) matching the CRM Controller
		/// pattern. These abstractions delegate to the service-scoped implementations
		/// registered in the CRM DI container.
		/// </summary>
		/// <param name="recordManager">CRM-scoped record CRUD operations for entity queries.</param>
		/// <param name="entityManager">Entity metadata operations for CRM entity lookups.</param>
		/// <param name="entityRelationManager">Relation traversal operations for intra-CRM relations.</param>
		/// <param name="logger">Structured logger for error reporting and diagnostics.</param>
		public CrmGrpcService(
			ICrmRecordOperations recordManager,
			ICrmEntityOperations entityManager,
			ICrmRelationOperations entityRelationManager,
			ILogger<CrmGrpcService> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_entityRelationManager = entityRelationManager ?? throw new ArgumentNullException(nameof(entityRelationManager));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		#endregion

		#region ===== Helper Methods =====

		/// <summary>
		/// Extracts the authenticated user from the gRPC call context by reading
		/// JWT claims from the underlying HttpContext. Follows the Core gRPC pattern.
		/// </summary>
		/// <param name="context">The gRPC server call context containing HTTP metadata.</param>
		/// <returns>The authenticated ErpUser extracted from JWT claims.</returns>
		/// <exception cref="RpcException">Thrown with Unauthenticated status if user cannot be extracted.</exception>
		private ErpUser ExtractUserFromContext(ServerCallContext context)
		{
			var httpContext = context.GetHttpContext();
			if (httpContext?.User == null)
			{
				throw new RpcException(new Status(StatusCode.Unauthenticated, "User not authenticated"));
			}

			var user = SecurityContext.ExtractUserFromClaims(httpContext.User.Claims);
			if (user == null)
			{
				throw new RpcException(new Status(StatusCode.Unauthenticated, "User not authenticated"));
			}

			return user;
		}

		/// <summary>
		/// Serializes a single EntityRecord to a JSON string for transport in gRPC messages.
		/// Uses NullValueHandling.Ignore to reduce payload size.
		/// </summary>
		/// <param name="record">The entity record to serialize.</param>
		/// <returns>JSON string representation of the record.</returns>
		private string SerializeEntityRecord(EntityRecord record)
		{
			if (record == null) return string.Empty;
			return JsonConvert.SerializeObject(record, SerializationSettings);
		}

		/// <summary>
		/// Serializes a list of EntityRecord objects to a JSON string for transport
		/// in gRPC messages. Used by bulk lookup methods.
		/// </summary>
		/// <param name="records">The list of entity records to serialize.</param>
		/// <returns>JSON array string representation of the records.</returns>
		private string SerializeEntityRecordList(List<EntityRecord> records)
		{
			if (records == null || records.Count == 0) return "[]";
			return JsonConvert.SerializeObject(records, SerializationSettings);
		}

		/// <summary>
		/// Validates that the specified entity name belongs to the CRM service boundary.
		/// Throws RpcException with InvalidArgument if the entity is not CRM-owned.
		/// </summary>
		/// <param name="entityName">The entity name to validate.</param>
		/// <exception cref="RpcException">Thrown if entity is not owned by CRM service.</exception>
		private void ValidateCrmEntity(string entityName)
		{
			if (string.IsNullOrWhiteSpace(entityName) || !CrmEntityNames.Contains(entityName))
			{
				throw new RpcException(new Status(
					StatusCode.InvalidArgument,
					$"Entity '{entityName}' is not owned by CRM service. Valid CRM entities: {string.Join(", ", CrmEntityNames)}"));
			}
		}

		/// <summary>
		/// Safely extracts a string value from an EntityRecord property.
		/// Returns empty string if the property is null or missing.
		/// </summary>
		/// <param name="record">The entity record to extract from.</param>
		/// <param name="fieldName">The field name to extract.</param>
		/// <returns>The string representation of the field value, or empty string.</returns>
		private static string SafeString(EntityRecord record, string fieldName)
		{
			try
			{
				var value = record[fieldName];
				return value?.ToString() ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>
		/// Safely extracts a DateTime value from an EntityRecord and converts it
		/// to a Google Protobuf Timestamp for proto message fields.
		/// </summary>
		/// <param name="record">The entity record to extract from.</param>
		/// <param name="fieldName">The field name to extract.</param>
		/// <returns>A Protobuf Timestamp, or null if the value is not a valid DateTime.</returns>
		private static Google.Protobuf.WellKnownTypes.Timestamp SafeTimestamp(EntityRecord record, string fieldName)
		{
			try
			{
				var value = record[fieldName];
				if (value is DateTime dt)
				{
					var utcDt = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
					return Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(utcDt);
				}
				if (value is DateTimeOffset dto)
				{
					return Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(dto);
				}
			}
			catch
			{
				// Gracefully handle conversion failures
			}
			return null;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated AccountRecord message.
		/// Extracts all account fields from the dynamic record into typed proto fields.
		/// </summary>
		/// <param name="record">The dynamic EntityRecord from RecordManager query results.</param>
		/// <returns>A populated AccountRecord proto message.</returns>
		private static AccountRecord MapToAccountRecord(EntityRecord record)
		{
			var account = new AccountRecord
			{
				Id = SafeString(record, "id"),
				Name = SafeString(record, "name"),
				XSearch = SafeString(record, "x_search"),
				LogoUrl = SafeString(record, "logo_url"),
				CurrencyId = SafeString(record, "currency_id"),
				CountryId = SafeString(record, "country_id"),
				Industry = SafeString(record, "industry"),
				Website = SafeString(record, "website"),
				Phone = SafeString(record, "fixed_phone"),
				Email = SafeString(record, "email"),
				Type = SafeString(record, "type"),
				Street = SafeString(record, "street"),
				Street2 = SafeString(record, "street_2"),
				City = SafeString(record, "city"),
				Region = SafeString(record, "region"),
				PostCode = SafeString(record, "post_code"),
				FixedPhone = SafeString(record, "fixed_phone"),
				MobilePhone = SafeString(record, "mobile_phone"),
				FaxPhone = SafeString(record, "fax_phone"),
				Notes = SafeString(record, "notes"),
				FirstName = SafeString(record, "first_name"),
				LastName = SafeString(record, "last_name"),
				TaxId = SafeString(record, "tax_id"),
				SalutationId = SafeString(record, "salutation_id"),
				LanguageId = SafeString(record, "language_id"),
				CreatedBy = SafeString(record, "created_by"),
				LastModifiedBy = SafeString(record, "last_modified_by")
			};

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) account.CreatedOn = createdOn;

			var lastModifiedOn = SafeTimestamp(record, "last_modified_on");
			if (lastModifiedOn != null) account.LastModifiedOn = lastModifiedOn;

			return account;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated ContactRecord message.
		/// </summary>
		private static ContactRecord MapToContactRecord(EntityRecord record)
		{
			var contact = new ContactRecord
			{
				Id = SafeString(record, "id"),
				FirstName = SafeString(record, "first_name"),
				LastName = SafeString(record, "last_name"),
				Email = SafeString(record, "email"),
				Phone = SafeString(record, "phone"),
				SalutationId = SafeString(record, "salutation_id"),
				AccountId = SafeString(record, "account_id"),
				XSearch = SafeString(record, "x_search"),
				CreatedBy = SafeString(record, "created_by"),
				LastModifiedBy = SafeString(record, "last_modified_by"),
				JobTitle = SafeString(record, "job_title"),
				Street = SafeString(record, "street"),
				Street2 = SafeString(record, "street_2"),
				City = SafeString(record, "city"),
				Region = SafeString(record, "region"),
				PostCode = SafeString(record, "post_code"),
				CountryId = SafeString(record, "country_id"),
				FixedPhone = SafeString(record, "fixed_phone"),
				MobilePhone = SafeString(record, "mobile_phone"),
				FaxPhone = SafeString(record, "fax_phone"),
				Notes = SafeString(record, "notes"),
				Photo = SafeString(record, "photo")
			};

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) contact.CreatedOn = createdOn;

			var lastModifiedOn = SafeTimestamp(record, "last_modified_on");
			if (lastModifiedOn != null) contact.LastModifiedOn = lastModifiedOn;

			return contact;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated CaseRecord message.
		/// </summary>
		private static CaseRecord MapToCaseRecord(EntityRecord record)
		{
			var caseRec = new CaseRecord
			{
				Id = SafeString(record, "id"),
				Subject = SafeString(record, "subject"),
				Description = SafeString(record, "description"),
				Status = SafeString(record, "status"),
				Priority = SafeString(record, "priority"),
				AccountId = SafeString(record, "account_id"),
				ContactId = SafeString(record, "contact_id"),
				XSearch = SafeString(record, "x_search"),
				CreatedBy = SafeString(record, "created_by"),
				LastModifiedBy = SafeString(record, "last_modified_by"),
				Number = SafeString(record, "number"),
				CaseTypeId = SafeString(record, "case_type_id"),
				CaseStatusId = SafeString(record, "case_status_id")
			};

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) caseRec.CreatedOn = createdOn;

			var lastModifiedOn = SafeTimestamp(record, "last_modified_on");
			if (lastModifiedOn != null) caseRec.LastModifiedOn = lastModifiedOn;

			return caseRec;
		}

		/// <summary>
		/// Converts SharedKernel ErrorModel list to proto-generated ErrorModel list.
		/// </summary>
		/// <param name="errors">Source errors from RecordManager responses.</param>
		/// <returns>List of proto-generated ErrorModel messages.</returns>
		private static List<GrpcErrorModel> MapErrors(List<ErrorModel> errors)
		{
			if (errors == null || errors.Count == 0) return new List<GrpcErrorModel>();
			return errors.Select(e => new GrpcErrorModel
			{
				Key = e.Key ?? string.Empty,
				Value = e.Value ?? string.Empty,
				Message = e.Message ?? string.Empty
			}).ToList();
		}

		#endregion

		#region ===== gRPC Override: Account Operations =====

		/// <summary>
		/// Retrieves a single account by ID. Used by Project service for
		/// account-project relation resolution (AAP 0.7.1).
		/// Proto: rpc GetAccount(GetAccountRequest) returns (GetAccountResponse)
		/// </summary>
		public override async Task<GetAccountResponse> GetAccount(
			GetAccountRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var accountId = Guid.Parse(request.Id);
					var query = new EntityQuery("account", "*", EntityQuery.QueryEQ("id", accountId));
					var response = _recordManager.Find(query);

					if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
					{
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Account not found with ID: {request.Id}"));
					}

					var accountRecord = MapToAccountRecord(response.Object.Data[0]);
					return await Task.FromResult(new GetAccountResponse
					{
						Success = true,
						Account = accountRecord
					});
				}
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetAccount), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid account ID format: {request.Id}"));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetAccount), ex.Message);
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetAccount), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== gRPC Override: Contact Operations =====

		/// <summary>
		/// Retrieves a single contact by ID. Used by Mail service for
		/// contact resolution (AAP 0.7.1).
		/// Proto: rpc GetContact(GetContactRequest) returns (GetContactResponse)
		/// </summary>
		public override async Task<GetContactResponse> GetContact(
			GetContactRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var contactId = Guid.Parse(request.Id);
					var query = new EntityQuery("contact", "*", EntityQuery.QueryEQ("id", contactId));
					var response = _recordManager.Find(query);

					if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
					{
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Contact not found with ID: {request.Id}"));
					}

					var contactRecord = MapToContactRecord(response.Object.Data[0]);
					return await Task.FromResult(new GetContactResponse
					{
						Success = true,
						Contact = contactRecord
					});
				}
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetContact), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid contact ID format: {request.Id}"));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetContact), ex.Message);
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetContact), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== gRPC Override: Case Operations =====

		/// <summary>
		/// Retrieves a single case by ID. Used by Project service for
		/// case-task linking (AAP 0.7.1).
		/// Proto: rpc GetCase(GetCaseRequest) returns (GetCaseResponse)
		/// </summary>
		public override async Task<GetCaseResponse> GetCase(
			GetCaseRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var caseId = Guid.Parse(request.Id);
					var query = new EntityQuery("case", "*", EntityQuery.QueryEQ("id", caseId));
					var response = _recordManager.Find(query);

					if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
					{
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Case not found with ID: {request.Id}"));
					}

					var caseRecord = MapToCaseRecord(response.Object.Data[0]);
					return await Task.FromResult(new GetCaseResponse
					{
						Success = true,
						CaseRecord = caseRecord
					});
				}
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetCase), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid case ID format: {request.Id}"));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetCase), ex.Message);
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetCase), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Public Methods: Single Entity Lookups =====

		/// <summary>
		/// Retrieves a single account record by ID. Convenience method for
		/// in-process use by CRM controllers and domain services.
		/// Used by Project service for account-project relation resolution.
		/// </summary>
		/// <param name="accountId">The account identifier (Guid as string).</param>
		/// <returns>The account EntityRecord, or null if not found.</returns>
		/// <exception cref="RpcException">Thrown with appropriate status codes on errors.</exception>
		public EntityRecord GetAccountById(string accountId)
		{
			try
			{
				var id = Guid.Parse(accountId);
				var query = new EntityQuery("account", "*", EntityQuery.QueryEQ("id", id));
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
				{
					throw new RpcException(new Status(StatusCode.NotFound,
						$"Account not found with ID: {accountId}"));
				}

				return response.Object.Data[0];
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetAccountById), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid account ID format: {accountId}"));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetAccountById), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves a single contact record by ID. Convenience method for
		/// in-process use. Used by Mail service for contact resolution.
		/// </summary>
		/// <param name="contactId">The contact identifier (Guid as string).</param>
		/// <returns>The contact EntityRecord, or null if not found.</returns>
		public EntityRecord GetContactById(string contactId)
		{
			try
			{
				var id = Guid.Parse(contactId);
				var query = new EntityQuery("contact", "*", EntityQuery.QueryEQ("id", id));
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
				{
					throw new RpcException(new Status(StatusCode.NotFound,
						$"Contact not found with ID: {contactId}"));
				}

				return response.Object.Data[0];
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetContactById), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid contact ID format: {contactId}"));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetContactById), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves a single case record by ID. Convenience method for
		/// in-process use. Used by Project service for case-task linking.
		/// </summary>
		/// <param name="caseId">The case identifier (Guid as string).</param>
		/// <returns>The case EntityRecord, or null if not found.</returns>
		public EntityRecord GetCaseById(string caseId)
		{
			try
			{
				var id = Guid.Parse(caseId);
				var query = new EntityQuery("case", "*", EntityQuery.QueryEQ("id", id));
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
				{
					throw new RpcException(new Status(StatusCode.NotFound,
						$"Case not found with ID: {caseId}"));
				}

				return response.Object.Data[0];
			}
			catch (RpcException)
			{
				throw;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetCaseById), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Invalid case ID format: {caseId}"));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetCaseById), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Public Methods: Bulk Entity Lookups =====

		/// <summary>
		/// Retrieves multiple account records by their IDs in a single query.
		/// Uses QueryOR to compose an efficient batch query against the account entity.
		/// Minimizes N+1 query patterns for Project service account resolution.
		/// </summary>
		/// <param name="accountIds">Collection of account identifiers (Guid strings).</param>
		/// <returns>List of found account EntityRecords. Missing IDs are silently excluded.</returns>
		public List<EntityRecord> GetAccountsByIds(IEnumerable<string> accountIds)
		{
			try
			{
				var ids = accountIds?.ToList();
				if (ids == null || ids.Count == 0)
					return new List<EntityRecord>();

				var queryConditions = ids
					.Select(id => EntityQuery.QueryEQ("id", Guid.Parse(id)))
					.ToArray();

				var queryFilter = queryConditions.Length == 1
					? queryConditions[0]
					: EntityQuery.QueryOR(queryConditions);

				var query = new EntityQuery("account", "*", queryFilter);
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null)
					return new List<EntityRecord>();

				return response.Object.Data;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetAccountsByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					"One or more account IDs have invalid format"));
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetAccountsByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves multiple contact records by their IDs in a single query.
		/// Uses QueryOR to compose an efficient batch query against the contact entity.
		/// Minimizes N+1 query patterns for Mail service contact resolution.
		/// </summary>
		/// <param name="contactIds">Collection of contact identifiers (Guid strings).</param>
		/// <returns>List of found contact EntityRecords.</returns>
		public List<EntityRecord> GetContactsByIds(IEnumerable<string> contactIds)
		{
			try
			{
				var ids = contactIds?.ToList();
				if (ids == null || ids.Count == 0)
					return new List<EntityRecord>();

				var queryConditions = ids
					.Select(id => EntityQuery.QueryEQ("id", Guid.Parse(id)))
					.ToArray();

				var queryFilter = queryConditions.Length == 1
					? queryConditions[0]
					: EntityQuery.QueryOR(queryConditions);

				var query = new EntityQuery("contact", "*", queryFilter);
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null)
					return new List<EntityRecord>();

				return response.Object.Data;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetContactsByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					"One or more contact IDs have invalid format"));
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetContactsByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves multiple case records by their IDs in a single query.
		/// Uses QueryOR to compose an efficient batch query against the case entity.
		/// Minimizes N+1 query patterns for Project service case-task resolution.
		/// </summary>
		/// <param name="caseIds">Collection of case identifiers (Guid strings).</param>
		/// <returns>List of found case EntityRecords.</returns>
		public List<EntityRecord> GetCasesByIds(IEnumerable<string> caseIds)
		{
			try
			{
				var ids = caseIds?.ToList();
				if (ids == null || ids.Count == 0)
					return new List<EntityRecord>();

				var queryConditions = ids
					.Select(id => EntityQuery.QueryEQ("id", Guid.Parse(id)))
					.ToArray();

				var queryFilter = queryConditions.Length == 1
					? queryConditions[0]
					: EntityQuery.QueryOR(queryConditions);

				var query = new EntityQuery("case", "*", queryFilter);
				var response = _recordManager.Find(query);

				if (!response.Success || response.Object?.Data == null)
					return new List<EntityRecord>();

				return response.Object.Data;
			}
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: Invalid ID format - {Message}",
					nameof(GetCasesByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					"One or more case IDs have invalid format"));
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(GetCasesByIds), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Public Methods: Generic CRM Record Query =====

		/// <summary>
		/// Executes an arbitrary query against a CRM-owned entity using the EQL engine.
		/// Validates entity ownership before executing to enforce service boundary integrity.
		///
		/// The queryJson parameter is deserialized using TypeNameHandling.Auto for
		/// polymorphic QueryObject support (AND, OR, EQ, CONTAINS, etc.).
		///
		/// This method enables flexible entity querying by other CRM components
		/// (controllers, event subscribers) and inter-service gRPC callers.
		/// </summary>
		/// <param name="entityName">The CRM entity to query (account, contact, case, etc.).</param>
		/// <param name="queryJson">JSON-serialized EntityQuery with TypeNameHandling.Auto support.</param>
		/// <returns>The full QueryResponse including field metadata and record data.</returns>
		/// <exception cref="RpcException">Thrown if entity is not CRM-owned or query fails.</exception>
		public QueryResponse FindCrmRecords(string entityName, string queryJson)
		{
			try
			{
				ValidateCrmEntity(entityName);

				EntityQuery entityQuery;
				if (!string.IsNullOrWhiteSpace(queryJson))
				{
					entityQuery = JsonConvert.DeserializeObject<EntityQuery>(queryJson, DeserializationSettings);
					if (entityQuery == null)
					{
						throw new RpcException(new Status(StatusCode.InvalidArgument,
							"Failed to deserialize query JSON into EntityQuery"));
					}
				}
				else
				{
					entityQuery = new EntityQuery(entityName);
				}

				// Ensure the entity name in the query matches the validated entity
				if (!string.Equals(entityQuery.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
				{
					entityQuery = new EntityQuery(
						entityName,
						entityQuery.Fields,
						entityQuery.Query,
						entityQuery.Sort,
						entityQuery.Skip,
						entityQuery.Limit);
				}

				var response = _recordManager.Find(entityQuery);
				return response;
			}
			catch (RpcException)
			{
				throw;
			}
			catch (ArgumentException ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(FindCrmRecords), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}", nameof(FindCrmRecords), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Public Methods: Bulk Entity Resolution =====

		/// <summary>
		/// Resolves multiple CRM entities in a single batch operation for denormalized
		/// data hydration. When Project or Mail service needs to hydrate denormalized
		/// UUIDs (e.g., account_id, contact_id) into full records, they call this method
		/// with multiple entity/ID pairs rather than making N individual gRPC calls.
		///
		/// Each request entry specifies an entity name, record ID, and optional field list.
		/// The method validates each entity belongs to the CRM service boundary,
		/// executes queries against the appropriate entity tables, and returns
		/// all resolved records grouped by entity name.
		/// </summary>
		/// <param name="entityRequests">
		/// Collection of tuples specifying (entityName, recordId, fields) for each
		/// entity to resolve. Fields defaults to "*" if empty.
		/// </param>
		/// <returns>
		/// Dictionary mapping entity names to lists of resolved EntityRecords.
		/// Missing records are silently excluded from results.
		/// </returns>
		/// <exception cref="RpcException">Thrown if any entity is not CRM-owned.</exception>
		public Dictionary<string, List<EntityRecord>> ResolveCrmEntities(
			IEnumerable<(string entityName, string recordId, string fields)> entityRequests)
		{
			var result = new Dictionary<string, List<EntityRecord>>(StringComparer.OrdinalIgnoreCase);

			try
			{
				if (entityRequests == null) return result;

				// Group requests by entity name for efficient batch querying
				var groupedRequests = entityRequests
					.GroupBy(r => r.entityName, StringComparer.OrdinalIgnoreCase)
					.ToList();

				foreach (var group in groupedRequests)
				{
					var entityName = group.Key;
					ValidateCrmEntity(entityName);

					var ids = group.Select(r =>
					{
						if (Guid.TryParse(r.recordId, out Guid parsedId))
							return (Guid?)parsedId;
						return null;
					})
					.Where(id => id.HasValue)
					.Select(id => id.Value)
					.ToList();

					if (ids.Count == 0) continue;

					// Determine fields to fetch (use first request's fields or default to "*")
					var fields = group.First().fields;
					if (string.IsNullOrWhiteSpace(fields)) fields = "*";

					// Build batch query with OR conditions for all IDs
					var queryConditions = ids
						.Select(id => EntityQuery.QueryEQ("id", id))
						.ToArray();

					var queryFilter = queryConditions.Length == 1
						? queryConditions[0]
						: EntityQuery.QueryOR(queryConditions);

					var query = new EntityQuery(entityName, fields, queryFilter);
					var response = _recordManager.Find(query);

					if (response.Success && response.Object?.Data != null)
					{
						if (!result.ContainsKey(entityName))
							result[entityName] = new List<EntityRecord>();

						result[entityName].AddRange(response.Object.Data);
					}
				}

				return result;
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {MethodName}: {Message}",
					nameof(ResolveCrmEntities), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion
	}
}
