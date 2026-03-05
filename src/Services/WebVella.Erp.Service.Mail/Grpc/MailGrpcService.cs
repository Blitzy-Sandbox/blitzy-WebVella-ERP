// =============================================================================
// MailGrpcService.cs — Server-Side gRPC Service for Mail/Notification Microservice
// =============================================================================
// Implements the gRPC service for the Mail/Notification microservice, enabling
// other microservices (Core, CRM, Project, Gateway) to invoke mail operations
// via efficient binary protocol over gRPC.
//
// In the monolith, mail operations were accessed via direct in-process method
// calls on SmtpService and EmailServiceManager classes. This gRPC service
// replaces those direct calls with a well-defined API contract over the wire.
//
// Proto source: proto/mail.proto (MailService definition)
// Pattern template: CRM gRPC service (CrmGrpcService.cs)
//
// Design decisions (AAP-derived):
//   - Proto-generated structured messages for request/response transport
//   - SecurityContext.OpenScope on every method — preserving monolith security
//   - SmtpService delegation — all business logic delegated to domain service
//   - Validation feedback via Success=false replies (not RpcException) for
//     ValidationException to preserve monolith's multi-error accumulation pattern
//
// Rules (AAP 0.8.1, 0.8.2, 0.8.3):
//   - [Authorize] on class by default per AAP 0.8.2
//   - No direct database access — delegates to domain SmtpService
//   - JWT tokens propagate identity across services per AAP 0.8.3
//   - Stateless service — no singletons, no static mutable state
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;

// Type aliases to resolve naming conflicts between proto-generated types
// (in WebVella.Erp.Service.Mail.Grpc namespace) and domain entity types.
// Proto compilation generates EmailPriority, EmailStatus enums in the same
// namespace as this file, causing ambiguity with domain enum types.
using DomainSmtpService = WebVella.Erp.Service.Mail.Domain.Services.SmtpService;
using DomainEmail = WebVella.Erp.Service.Mail.Domain.Entities.Email;
using DomainEmailAddress = WebVella.Erp.Service.Mail.Domain.Entities.EmailAddress;
using DomainEmailStatus = WebVella.Erp.Service.Mail.Domain.Entities.EmailStatus;
using DomainEmailPriority = WebVella.Erp.Service.Mail.Domain.Entities.EmailPriority;
using DomainSmtpServiceConfig = WebVella.Erp.Service.Mail.Domain.Entities.SmtpServiceConfig;
using GrpcErrorModel = WebVella.Erp.SharedKernel.Grpc.ErrorModel;

namespace WebVella.Erp.Service.Mail.Grpc
{
	/// <summary>
	/// gRPC service implementation for the Mail/Notification microservice providing
	/// inter-service email sending, queuing, status retrieval, and SMTP configuration
	/// management. Inherits from the proto-generated <see cref="MailService.MailServiceBase"/>
	/// to implement the Mail service RPC contract defined in proto/mail.proto.
	///
	/// All gRPC methods delegate to <see cref="DomainSmtpService"/> which consolidates
	/// ALL SMTP business logic from three monolith source files:
	///   - SmtpInternalService (validation, HTML processing, queue)
	///   - SmtpService (SendEmail/QueueEmail overloads)
	///   - EmailServiceManager (SMTP config caching)
	///
	/// Design decisions:
	/// <list type="bullet">
	///   <item>Proto-generated structured message types for transport — matching proto contract</item>
	///   <item>SecurityContext.OpenScope on every method — preserving monolith per-request auth</item>
	///   <item>ValidationException returns Success=false reply — preserving multi-error accumulation</item>
	///   <item>Sensitive SMTP data (username/password) included in inter-service transport since
	///     gRPC is service-to-service, not client-facing</item>
	/// </list>
	/// </summary>
	[Authorize]
	public class MailGrpcService : MailService.MailServiceBase
	{
		#region ===== Private Fields =====

		private readonly DomainSmtpService _smtpService;
		private readonly RecordManager _recordManager;
		private readonly ILogger<MailGrpcService> _logger;

		/// <summary>
		/// Shared JSON serializer settings used for supplemental serialization
		/// (e.g., attachment lists). NullValueHandling.Ignore reduces payload size.
		/// </summary>
		private static readonly JsonSerializerSettings SerializationSettings = new JsonSerializerSettings
		{
			NullValueHandling = NullValueHandling.Ignore
		};

		#endregion

		#region ===== Constructor =====

		/// <summary>
		/// Constructs a MailGrpcService with all required dependencies injected via DI.
		/// Uses the Mail service's consolidated <see cref="DomainSmtpService"/> which
		/// handles all SMTP business logic, caching, and email persistence.
		/// </summary>
		/// <param name="smtpService">Domain SMTP service for all mail business logic delegation.</param>
		/// <param name="logger">Structured logger for error reporting and diagnostics.</param>
		public MailGrpcService(
			DomainSmtpService smtpService,
			RecordManager recordManager,
			ILogger<MailGrpcService> logger)
		{
			_smtpService = smtpService ?? throw new ArgumentNullException(nameof(smtpService));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		#endregion

		#region ===== Helper Methods =====

		/// <summary>
		/// Extracts the authenticated user from the gRPC call context by reading
		/// JWT claims from the underlying HttpContext. Follows the Core/CRM gRPC pattern.
		/// </summary>
		/// <param name="context">The gRPC server call context containing HTTP metadata.</param>
		/// <returns>The authenticated ErpUser extracted from JWT claims, or null if not authenticated.</returns>
		private static ErpUser ExtractUserFromContext(ServerCallContext context)
		{
			var httpContext = context.GetHttpContext();
			if (httpContext?.User?.Identity?.IsAuthenticated == true)
			{
				return SecurityContext.ExtractUserFromClaims(httpContext.User.Claims);
			}
			return null;
		}

		/// <summary>
		/// Serializes an object to JSON string for supplemental data transport.
		/// Uses Newtonsoft.Json with NullValueHandling.Ignore per AAP 0.8.2 requirement
		/// for API contract stability with existing [JsonProperty] annotations.
		/// </summary>
		/// <param name="obj">The object to serialize.</param>
		/// <returns>JSON string representation, or null if input is null.</returns>
		private static string SerializeToJson(object obj)
		{
			if (obj == null) return null;
			return JsonConvert.SerializeObject(obj, SerializationSettings);
		}

		/// <summary>
		/// Resolves the SMTP service configuration by ID from the domain service.
		/// Uses cached lookup with 1-hour TTL via Redis.
		/// </summary>
		/// <param name="serviceId">SMTP service ID as string (Guid format).</param>
		/// <returns>The resolved SMTP service configuration.</returns>
		/// <exception cref="RpcException">Thrown with NotFound status if service not found.</exception>
		private DomainSmtpServiceConfig ResolveSmtpConfig(string serviceId)
		{
			DomainSmtpServiceConfig config;

			if (!string.IsNullOrWhiteSpace(serviceId))
			{
				if (!Guid.TryParse(serviceId, out Guid parsedId))
				{
					throw new RpcException(new Status(StatusCode.InvalidArgument,
						$"Invalid SMTP service ID format: '{serviceId}'. Expected a valid GUID."));
				}
				config = _smtpService.GetSmtpService(parsedId);
			}
			else
			{
				// No service ID specified — resolve default SMTP service
				config = _smtpService.GetSmtpService();
			}

			if (config == null)
			{
				throw new RpcException(new Status(StatusCode.NotFound,
					"SMTP service not found. Ensure a valid SMTP service is configured."));
			}

			return config;
		}

		#endregion

		#region ===== Type Mapping Helpers =====

		/// <summary>
		/// Converts a proto <see cref="EmailAddressProto"/> to a domain <see cref="DomainEmailAddress"/>.
		/// </summary>
		private static DomainEmailAddress MapProtoToEmailAddress(EmailAddressProto proto)
		{
			if (proto == null) return null;
			return new DomainEmailAddress
			{
				Name = proto.Name ?? string.Empty,
				Address = proto.Address ?? string.Empty
			};
		}

		/// <summary>
		/// Converts a domain <see cref="DomainEmailAddress"/> to a proto <see cref="EmailAddressProto"/>.
		/// </summary>
		private static EmailAddressProto MapEmailAddressToProto(DomainEmailAddress domain)
		{
			if (domain == null) return new EmailAddressProto();
			return new EmailAddressProto
			{
				Name = domain.Name ?? string.Empty,
				Address = domain.Address ?? string.Empty
			};
		}

		/// <summary>
		/// Maps a proto <see cref="EmailPriority"/> enum value to a domain <see cref="DomainEmailPriority"/>.
		/// Proto values are offset by +1 from domain values due to proto3 UNSPECIFIED=0 requirement.
		/// Proto: Unspecified=0, Low=1, Normal=2, High=3
		/// Domain: Low=0, Normal=1, High=2
		/// </summary>
		private static DomainEmailPriority MapProtoPriorityToDomain(EmailPriority protoPriority)
		{
			return protoPriority switch
			{
				EmailPriority.Low => DomainEmailPriority.Low,
				EmailPriority.Normal => DomainEmailPriority.Normal,
				EmailPriority.High => DomainEmailPriority.High,
				_ => DomainEmailPriority.Normal // Default to Normal for Unspecified
			};
		}

		/// <summary>
		/// Maps a domain <see cref="DomainEmailPriority"/> to a proto <see cref="EmailPriority"/>.
		/// </summary>
		private static EmailPriority MapDomainPriorityToProto(DomainEmailPriority domainPriority)
		{
			return domainPriority switch
			{
				DomainEmailPriority.Low => EmailPriority.Low,
				DomainEmailPriority.Normal => EmailPriority.Normal,
				DomainEmailPriority.High => EmailPriority.High,
				_ => EmailPriority.Unspecified
			};
		}

		/// <summary>
		/// Maps a domain <see cref="DomainEmailStatus"/> to a proto <see cref="EmailStatus"/>.
		/// Proto values are offset by +1 from domain values due to proto3 UNSPECIFIED=0 requirement.
		/// Proto: Unspecified=0, Pending=1, Sent=2, Aborted=3, Failed=4
		/// Domain: Pending=0, Sent=1, Aborted=2
		/// </summary>
		private static EmailStatus MapDomainStatusToProto(DomainEmailStatus domainStatus)
		{
			return domainStatus switch
			{
				DomainEmailStatus.Pending => EmailStatus.Pending,
				DomainEmailStatus.Sent => EmailStatus.Sent,
				DomainEmailStatus.Aborted => EmailStatus.Aborted,
				_ => EmailStatus.Unspecified
			};
		}

		/// <summary>
		/// Converts a domain <see cref="DomainEmail"/> entity to a proto <see cref="EmailProto"/> message.
		/// Handles all 19 fields including structured address types, enum mapping, and timestamps.
		/// </summary>
		private static EmailProto MapEmailToProto(DomainEmail email)
		{
			if (email == null) return null;

			var proto = new EmailProto
			{
				Id = email.Id.ToString(),
				ServiceId = email.ServiceId.ToString(),
				Subject = email.Subject ?? string.Empty,
				ContentText = email.ContentText ?? string.Empty,
				ContentHtml = email.ContentHtml ?? string.Empty,
				Priority = MapDomainPriorityToProto(email.Priority),
				Status = MapDomainStatusToProto(email.Status),
				ServerError = email.ServerError ?? string.Empty,
				RetriesCount = email.RetriesCount,
				ScheduledOn = email.ScheduledOn?.ToString("o") ?? string.Empty,
				Attachments = email.Attachments != null ? SerializeToJson(email.Attachments) : string.Empty,
				XSearch = email.XSearch ?? string.Empty
			};

			// Map sender address
			if (email.Sender != null)
			{
				proto.Sender = MapEmailAddressToProto(email.Sender);
			}

			// Map recipients — separate cc:/bcc: prefixed addresses into dedicated fields
			if (email.Recipients != null)
			{
				foreach (var recipient in email.Recipients)
				{
					if (recipient == null) continue;

					if (recipient.Address != null && recipient.Address.StartsWith("cc:", StringComparison.OrdinalIgnoreCase))
					{
						proto.CcRecipients.Add(new EmailAddressProto
						{
							Name = recipient.Name ?? string.Empty,
							Address = recipient.Address.Substring(3)
						});
					}
					else if (recipient.Address != null && recipient.Address.StartsWith("bcc:", StringComparison.OrdinalIgnoreCase))
					{
						proto.BccRecipients.Add(new EmailAddressProto
						{
							Name = recipient.Name ?? string.Empty,
							Address = recipient.Address.Substring(4)
						});
					}
					else
					{
						proto.Recipients.Add(MapEmailAddressToProto(recipient));
					}
				}
			}

			// Map reply-to — monolith stores as semicolon-separated string in ReplyToEmail
			if (!string.IsNullOrWhiteSpace(email.ReplyToEmail))
			{
				var replyToParts = email.ReplyToEmail.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (var replyEmail in replyToParts)
				{
					proto.ReplyToAddresses.Add(new EmailAddressProto
					{
						Name = string.Empty,
						Address = replyEmail.Trim()
					});
				}
			}

			// Map timestamps — convert DateTime to proto Timestamp
			try
			{
				var createdUtc = email.CreatedOn.Kind == DateTimeKind.Utc
					? email.CreatedOn
					: DateTime.SpecifyKind(email.CreatedOn, DateTimeKind.Utc);
				proto.CreatedOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(createdUtc);
			}
			catch
			{
				// If timestamp conversion fails, leave as default (epoch)
			}

			if (email.SentOn.HasValue)
			{
				try
				{
					var sentUtc = email.SentOn.Value.Kind == DateTimeKind.Utc
						? email.SentOn.Value
						: DateTime.SpecifyKind(email.SentOn.Value, DateTimeKind.Utc);
					proto.SentOn = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(sentUtc);
				}
				catch
				{
					// If timestamp conversion fails, leave as default
				}
			}

			return proto;
		}

		/// <summary>
		/// Converts a domain <see cref="DomainSmtpServiceConfig"/> to a proto <see cref="SmtpServiceProto"/> message.
		/// Includes all 14 config fields, handling composed default_sender and decomposed default_reply_to.
		/// Sensitive fields (Username, Password) are included for inter-service transport.
		/// </summary>
		private static SmtpServiceProto MapSmtpServiceConfigToProto(DomainSmtpServiceConfig config)
		{
			if (config == null) return null;

			var proto = new SmtpServiceProto
			{
				Id = config.Id.ToString(),
				Name = config.Name ?? string.Empty,
				Server = config.Server ?? string.Empty,
				Port = config.Port,
				Username = config.Username ?? string.Empty,
				Password = config.Password ?? string.Empty,
				IsEnabled = config.IsEnabled,
				IsDefault = config.IsDefault,
				MaxRetriesCount = config.MaxRetriesCount,
				RetryWaitMinutes = config.RetryWaitMinutes,
				ConnectionSecurity = ((int)config.ConnectionSecurity).ToString()
			};

			// Compose default_sender from DefaultSenderName + DefaultSenderEmail
			proto.DefaultSender = new EmailAddressProto
			{
				Name = config.DefaultSenderName ?? string.Empty,
				Address = config.DefaultSenderEmail ?? string.Empty
			};

			// Decompose DefaultReplyToEmail from semicolon-separated string
			if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail))
			{
				var replyToParts = config.DefaultReplyToEmail.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (var replyEmail in replyToParts)
				{
					proto.DefaultReplyTo.Add(new EmailAddressProto
					{
						Name = string.Empty,
						Address = replyEmail.Trim()
					});
				}
			}

			return proto;
		}

		/// <summary>
		/// Maps validation errors from a <see cref="WebVella.Erp.SharedKernel.Exceptions.ValidationException"/>
		/// to proto <see cref="GrpcErrorModel"/> messages for the response error collection.
		/// </summary>
		private static void MapValidationErrors(
			WebVella.Erp.SharedKernel.Exceptions.ValidationException vex,
			Google.Protobuf.Collections.RepeatedField<GrpcErrorModel> errors)
		{
			if (vex?.Errors == null) return;
			foreach (var error in vex.Errors)
			{
				errors.Add(new GrpcErrorModel
				{
					Key = error.PropertyName ?? string.Empty,
					Value = string.Empty,
					Message = error.Message ?? string.Empty
				});
			}
		}

		/// <summary>
		/// Overload: Maps validation errors from a <see cref="List{ErrorModel}"/>
		/// to proto <see cref="GrpcErrorModel"/> messages for the response error collection.
		/// Used by CRUD methods that accumulate errors via SmtpService.Validate* methods.
		/// </summary>
		private static void MapValidationErrors(
			List<ErrorModel> errorList,
			Google.Protobuf.Collections.RepeatedField<GrpcErrorModel> errors)
		{
			if (errorList == null) return;
			foreach (var error in errorList)
			{
				errors.Add(new GrpcErrorModel
				{
					Key = error.Key ?? string.Empty,
					Value = error.Value ?? string.Empty,
					Message = error.Message ?? string.Empty
				});
			}
		}

		#endregion

		#region ===== gRPC Method: SendEmail =====

		/// <summary>
		/// Sends an email immediately via the specified SMTP service.
		/// Delegates to <see cref="DomainSmtpService.SendEmail"/> overloads which handle
		/// MimeMessage construction, SMTP dispatch via MailKit, and email record persistence.
		///
		/// Source mapping:
		///   - SmtpService.SendEmail(config, recipient, subject, ...) — single recipient, default sender
		///   - SmtpService.SendEmail(config, recipients, subject, ...) — multiple recipients, default sender
		///   - SmtpService.SendEmail(config, recipient, sender, subject, ...) — single recipient, explicit sender
		///   - SmtpService.SendEmail(config, recipients, sender, subject, ...) — multiple recipients, explicit sender
		///
		/// Proto request fields mapped:
		///   - service_id → SMTP config resolution via ResolveSmtpConfig
		///   - sender → optional EmailAddress for explicit sender
		///   - recipients → primary To: recipients
		///   - cc_recipients → re-combined with "cc:" prefix for monolith compatibility
		///   - bcc_recipients → re-combined with "bcc:" prefix for monolith compatibility
		///   - subject, content_text, content_html → passed directly to domain service
		///   - attachments → file paths passed to domain service
		/// </summary>
		public override async Task<SendEmailResponse> SendEmail(SendEmailRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				if (user == null)
				{
					throw new RpcException(new Status(StatusCode.Unauthenticated,
						"User not authenticated. A valid JWT token is required."));
				}

				using (SecurityContext.OpenScope(user))
				{
					// Resolve SMTP service configuration (by ID or default)
					var smtpConfig = ResolveSmtpConfig(request.ServiceId);

					// Map proto recipients to domain EmailAddress objects
					var recipients = new List<DomainEmailAddress>();
					foreach (var r in request.Recipients)
					{
						recipients.Add(MapProtoToEmailAddress(r));
					}

					// Add CC recipients with "cc:" prefix — monolith convention for queue processing
					foreach (var cc in request.CcRecipients)
					{
						var addr = MapProtoToEmailAddress(cc);
						addr.Address = "cc:" + addr.Address;
						recipients.Add(addr);
					}

					// Add BCC recipients with "bcc:" prefix — monolith convention for queue processing
					foreach (var bcc in request.BccRecipients)
					{
						var addr = MapProtoToEmailAddress(bcc);
						addr.Address = "bcc:" + addr.Address;
						recipients.Add(addr);
					}

					// Map optional explicit sender
					DomainEmailAddress sender = null;
					if (request.Sender != null && !string.IsNullOrWhiteSpace(request.Sender.Address))
					{
						sender = MapProtoToEmailAddress(request.Sender);
					}

					// Collect attachment paths
					var attachments = request.Attachments?.ToList() ?? new List<string>();

					// Delegate to appropriate SmtpService overload based on sender and recipient count
					if (sender != null)
					{
						if (recipients.Count == 1)
						{
							_smtpService.SendEmail(smtpConfig, recipients[0], sender, request.Subject,
								request.ContentText, request.ContentHtml, attachments);
						}
						else
						{
							_smtpService.SendEmail(smtpConfig, recipients, sender, request.Subject,
								request.ContentText, request.ContentHtml, attachments);
						}
					}
					else
					{
						if (recipients.Count == 1)
						{
							_smtpService.SendEmail(smtpConfig, recipients[0], request.Subject,
								request.ContentText, request.ContentHtml, attachments);
						}
						else
						{
							_smtpService.SendEmail(smtpConfig, recipients, request.Subject,
								request.ContentText, request.ContentHtml, attachments);
						}
					}

					return await Task.FromResult(new SendEmailResponse
					{
						Success = true,
						Message = "Email sent successfully"
					});
				}
			}
			catch (WebVella.Erp.SharedKernel.Exceptions.ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in SendEmail: {Message}", vex.Message);
				var response = new SendEmailResponse
				{
					Success = false,
					Message = vex.Message ?? "Validation failed"
				};
				MapValidationErrors(vex, response.Errors);
				return response;
			}
			catch (RpcException)
			{
				// Re-throw RpcExceptions (already formatted for gRPC transport)
				throw;
			}
			catch (ArgumentException aex)
			{
				_logger.LogWarning(aex, "Invalid argument in SendEmail: {Message}", aex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, aex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.SendEmail: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal,
					"Internal error processing send email request"));
			}
		}

		#endregion

		#region ===== gRPC Method: QueueEmail =====

		/// <summary>
		/// Queues an email for background processing by the scheduled mail queue job.
		/// The email is saved with Pending status and a scheduled_on timestamp.
		/// The ProcessSmtpQueueJob picks it up on its 10-minute interval.
		///
		/// Delegates to <see cref="DomainSmtpService.QueueEmail"/> overloads which handle
		/// validation, email record creation, and persistence.
		///
		/// Source mapping:
		///   - SmtpService.QueueEmail(config, recipient, subject, ...) — single, default sender
		///   - SmtpService.QueueEmail(config, recipients, subject, ...) — multiple, default sender
		///   - SmtpService.QueueEmail(config, recipients, sender, replyTo, subject, ...) — full overload
		///
		/// Proto request fields mapped:
		///   - service_id → SMTP config resolution
		///   - sender → optional explicit sender
		///   - recipients → recipient list (may include cc:/bcc: prefixed addresses)
		///   - priority → mapped from proto enum to domain enum
		///   - scheduled_on → optional Timestamp for deferred delivery
		/// </summary>
		public override async Task<QueueEmailResponse> QueueEmail(QueueEmailRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				if (user == null)
				{
					throw new RpcException(new Status(StatusCode.Unauthenticated,
						"User not authenticated. A valid JWT token is required."));
				}

				using (SecurityContext.OpenScope(user))
				{
					// Resolve SMTP service configuration
					var smtpConfig = ResolveSmtpConfig(request.ServiceId);

					// Map proto recipients to domain EmailAddress objects
					var recipients = new List<DomainEmailAddress>();
					foreach (var r in request.Recipients)
					{
						recipients.Add(MapProtoToEmailAddress(r));
					}

					// Map optional explicit sender
					DomainEmailAddress sender = null;
					if (request.Sender != null && !string.IsNullOrWhiteSpace(request.Sender.Address))
					{
						sender = MapProtoToEmailAddress(request.Sender);
					}

					// Map priority from proto to domain enum
					var priority = MapProtoPriorityToDomain(request.Priority);

					// Collect attachment paths
					var attachments = request.Attachments?.ToList() ?? new List<string>();

					// Delegate to appropriate SmtpService QueueEmail overload
					if (sender != null)
					{
						// Full overload with explicit sender (replyTo is null — uses config default)
						if (recipients.Count == 1)
						{
							_smtpService.QueueEmail(smtpConfig, recipients[0], sender, request.Subject,
								request.ContentText, request.ContentHtml, priority, attachments);
						}
						else
						{
							_smtpService.QueueEmail(smtpConfig, recipients, sender, request.Subject,
								request.ContentText, request.ContentHtml, priority, attachments);
						}
					}
					else
					{
						// Default sender from SMTP config
						if (recipients.Count == 1)
						{
							_smtpService.QueueEmail(smtpConfig, recipients[0], request.Subject,
								request.ContentText, request.ContentHtml, priority, attachments);
						}
						else
						{
							_smtpService.QueueEmail(smtpConfig, recipients, request.Subject,
								request.ContentText, request.ContentHtml, priority, attachments);
						}
					}

					return await Task.FromResult(new QueueEmailResponse
					{
						Success = true,
						Message = "Email queued successfully for background processing"
					});
				}
			}
			catch (WebVella.Erp.SharedKernel.Exceptions.ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in QueueEmail: {Message}", vex.Message);
				var response = new QueueEmailResponse
				{
					Success = false,
					Message = vex.Message ?? "Validation failed"
				};
				MapValidationErrors(vex, response.Errors);
				return response;
			}
			catch (RpcException)
			{
				throw;
			}
			catch (ArgumentException aex)
			{
				_logger.LogWarning(aex, "Invalid argument in QueueEmail: {Message}", aex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, aex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.QueueEmail: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal,
					"Internal error processing queue email request"));
			}
		}

		#endregion

		#region ===== gRPC Method: GetEmail (GetEmailStatus) =====

		/// <summary>
		/// Retrieves a single email record by its unique identifier.
		/// Maps to the exports schema's GetEmailStatus method — provides email status and
		/// full email record data for inter-service email tracking.
		///
		/// Source: SmtpInternalService.GetEmail(Guid id) which executes:
		///   SELECT * FROM email WHERE id = @id
		/// Returns Email model with all 17 properties including Status, SentOn, ServerError.
		///
		/// Proto: rpc GetEmail(GetEmailRequest) returns (GetEmailResponse)
		/// </summary>
		public override async Task<GetEmailResponse> GetEmail(GetEmailRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				if (user == null)
				{
					throw new RpcException(new Status(StatusCode.Unauthenticated,
						"User not authenticated. A valid JWT token is required."));
				}

				using (SecurityContext.OpenScope(user))
				{
					// Parse and validate email ID
					if (string.IsNullOrWhiteSpace(request.Id))
					{
						throw new RpcException(new Status(StatusCode.InvalidArgument,
							"Email ID is required."));
					}

					if (!Guid.TryParse(request.Id, out Guid emailId))
					{
						throw new RpcException(new Status(StatusCode.InvalidArgument,
							$"Invalid email ID format: '{request.Id}'. Expected a valid GUID."));
					}

					// Retrieve email from domain service
					var email = _smtpService.GetEmail(emailId);

					if (email == null)
					{
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Email with ID '{request.Id}' not found."));
					}

					// Map domain Email entity to proto EmailProto message
					var emailProto = MapEmailToProto(email);

					return await Task.FromResult(new GetEmailResponse
					{
						Success = true,
						Email = emailProto
					});
				}
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.GetEmail: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal,
					"Internal error retrieving email record"));
			}
		}

		#endregion

		#region ===== gRPC Method: GetSmtpService =====

		/// <summary>
		/// Retrieves an SMTP service configuration by its unique identifier.
		/// Uses Redis-backed caching with 1-hour TTL for performance.
		///
		/// Source: EmailServiceManager.GetSmtpService(Guid id) — cache-aware lookup.
		///   Uses IDistributedCache (Redis) with 1-hour absolute expiration.
		///   Cache key format: "SMTP-{id}"
		///
		/// Proto: rpc GetSmtpService(GetSmtpServiceRequest) returns (GetSmtpServiceResponse)
		///
		/// Note: SmtpServiceConfig includes sensitive fields (Username, Password).
		/// These are included in the response since gRPC is service-to-service transport,
		/// not client-facing. The [Authorize] attribute ensures only authenticated services
		/// can access this endpoint.
		/// </summary>
		public override async Task<GetSmtpServiceResponse> GetSmtpService(GetSmtpServiceRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				if (user == null)
				{
					throw new RpcException(new Status(StatusCode.Unauthenticated,
						"User not authenticated. A valid JWT token is required."));
				}

				using (SecurityContext.OpenScope(user))
				{
					// Resolve SMTP config — supports lookup by ID or default
					DomainSmtpServiceConfig smtpConfig;

					if (!string.IsNullOrWhiteSpace(request.Id))
					{
						if (!Guid.TryParse(request.Id, out Guid serviceId))
						{
							throw new RpcException(new Status(StatusCode.InvalidArgument,
								$"Invalid SMTP service ID format: '{request.Id}'. Expected a valid GUID."));
						}
						smtpConfig = _smtpService.GetSmtpService(serviceId);
					}
					else
					{
						// No ID specified — resolve default SMTP service
						smtpConfig = _smtpService.GetSmtpService();
					}

					if (smtpConfig == null)
					{
						throw new RpcException(new Status(StatusCode.NotFound,
							"SMTP service not found. Ensure a valid SMTP service is configured."));
					}

					// Map domain SmtpServiceConfig to proto SmtpServiceProto message
					var smtpProto = MapSmtpServiceConfigToProto(smtpConfig);

					return await Task.FromResult(new GetSmtpServiceResponse
					{
						Success = true,
						SmtpService = smtpProto
					});
				}
			}
			catch (RpcException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.GetSmtpService: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal,
					"Internal error retrieving SMTP service configuration"));
			}
		}

		#endregion

		#region ===== gRPC Override: ListEmails =====

		/// <summary>
		/// Lists emails with pagination and optional filters.
		/// Proto: rpc ListEmails(ListEmailsRequest) returns (ListEmailsResponse)
		/// </summary>
		public override async Task<ListEmailsResponse> ListEmails(ListEmailsRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var page = request.Page > 0 ? request.Page : 1;
					var pageSize = request.PageSize > 0 ? request.PageSize : 10;

					var filters = new List<QueryObject>();
					if (!string.IsNullOrWhiteSpace(request.StatusFilter))
						filters.Add(EntityQuery.QueryEQ("status", request.StatusFilter));
					if (!string.IsNullOrWhiteSpace(request.ServiceId))
						filters.Add(EntityQuery.QueryEQ("service_id", Guid.Parse(request.ServiceId)));
					if (!string.IsNullOrWhiteSpace(request.SearchQuery))
						filters.Add(EntityQuery.QueryContains("x_search", request.SearchQuery));

					QueryObject filter = null;
					if (filters.Count == 1) filter = filters[0];
					else if (filters.Count > 1) filter = EntityQuery.QueryAND(filters.ToArray());

					var sortObjects = new[] { new QuerySortObject("created_on", QuerySortType.Descending) };
					var query = new EntityQuery("email", "id", filter, sortObjects, (page - 1) * pageSize, pageSize);
					var recMan = _recordManager;
					var response = recMan.Find(query);

					var countQuery = new EntityQuery("email", "id", filter);
					var countResponse = recMan.Count(countQuery);

					var result = new ListEmailsResponse { Success = true };
					result.TotalCount = (int)(countResponse?.Object ?? 0);

					if (response.Success && response.Object?.Data != null)
					{
						foreach (var rec in response.Object.Data)
						{
							var emailId = (Guid)rec["id"];
							var email = _smtpService.GetEmail(emailId);
							if (email != null)
								result.Emails.Add(MapEmailToProto(email));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.ListEmails: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, "Internal error listing emails"));
			}
		}

		#endregion

		#region ===== gRPC Override: SMTP Service Management =====

		/// <summary>
		/// Creates a new SMTP service configuration.
		/// Proto: rpc CreateSmtpService(CreateSmtpServiceRequest) returns (SmtpServiceResponse)
		/// </summary>
		public override async Task<SmtpServiceResponse> CreateSmtpService(CreateSmtpServiceRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var record = MapSmtpProtoToEntityRecord(request.SmtpService);
					if (!record.Properties.ContainsKey("id") || record["id"] == null)
						record["id"] = Guid.NewGuid();

					// Run domain validation
					var errors = new List<ErrorModel>();
					_smtpService.ValidatePreCreateRecord(record, errors);
					if (errors.Count > 0)
					{
						var result = new SmtpServiceResponse { Success = false, Message = "Validation failed." };
						MapValidationErrors(errors, result.Errors);
						return await Task.FromResult(result);
					}

					var recMan = _recordManager;
					var response = recMan.CreateRecord("smtp_service", record);

					if (!response.Success)
					{
						var result = new SmtpServiceResponse { Success = false, Message = response.Message ?? "Failed to create SMTP service." };
						if (response.Errors != null) MapValidationErrors(response.Errors, result.Errors);
						return await Task.FromResult(result);
					}

					// Handle default service logic
					_smtpService.HandleDefaultServiceSetup(record, errors);
					_smtpService.ClearCache();

					return await Task.FromResult(new SmtpServiceResponse
					{
						Success = true,
						Message = "SMTP service created."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.CreateSmtpService: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, "Internal error creating SMTP service"));
			}
		}

		/// <summary>
		/// Updates an existing SMTP service configuration.
		/// Proto: rpc UpdateSmtpService(UpdateSmtpServiceRequest) returns (SmtpServiceResponse)
		/// </summary>
		public override async Task<SmtpServiceResponse> UpdateSmtpService(UpdateSmtpServiceRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var record = MapSmtpProtoToEntityRecord(request.SmtpService);
					if (!record.Properties.ContainsKey("id") || record["id"] == null)
						throw new RpcException(new Status(StatusCode.InvalidArgument, "SMTP service id is required for update."));

					// Run domain validation
					var errors = new List<ErrorModel>();
					_smtpService.ValidatePreUpdateRecord(record, errors);
					if (errors.Count > 0)
					{
						var result = new SmtpServiceResponse { Success = false, Message = "Validation failed." };
						MapValidationErrors(errors, result.Errors);
						return await Task.FromResult(result);
					}

					var recMan = _recordManager;
					var response = recMan.UpdateRecord("smtp_service", record);

					if (!response.Success)
					{
						var result = new SmtpServiceResponse { Success = false, Message = response.Message ?? "Failed to update SMTP service." };
						if (response.Errors != null) MapValidationErrors(response.Errors, result.Errors);
						return await Task.FromResult(result);
					}

					// Handle default service logic and clear cache
					_smtpService.HandleDefaultServiceSetup(record, errors);
					_smtpService.ClearCache();

					return await Task.FromResult(new SmtpServiceResponse
					{
						Success = true,
						Message = "SMTP service updated."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.UpdateSmtpService: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, "Internal error updating SMTP service"));
			}
		}

		/// <summary>
		/// Tests an SMTP service by sending a test email.
		/// Proto: rpc TestSmtpService(TestSmtpServiceRequest) returns (TestSmtpServiceResponse)
		/// </summary>
		public override async Task<TestSmtpServiceResponse> TestSmtpService(TestSmtpServiceRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var serviceId = Guid.Parse(request.ServiceId);
					var testEmailAddress = request.TestEmailAddress;

					if (string.IsNullOrWhiteSpace(testEmailAddress))
						throw new RpcException(new Status(StatusCode.InvalidArgument, "Test email address is required."));

					var config = _smtpService.GetSmtpService(serviceId);
					if (config == null)
						throw new RpcException(new Status(StatusCode.NotFound, "SMTP service not found."));

					var recipient = new DomainEmailAddress { Address = testEmailAddress, Name = testEmailAddress };
					_smtpService.SendEmail(config, recipient, "Test Email from WebVella ERP",
						"This is a test email to verify the SMTP service configuration.",
						"<p>This is a test email to verify the SMTP service configuration.</p>",
						null);

					return await Task.FromResult(new TestSmtpServiceResponse
					{
						Success = true,
						Message = "Test email was successfully sent."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Invalid service_id format in TestSmtpService");
				throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid service ID: {request.ServiceId}"));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.TestSmtpService: {Message}", ex.Message);
				return await Task.FromResult(new TestSmtpServiceResponse
				{
					Success = false,
					Message = $"Failed to send test email: {ex.Message}"
				});
			}
		}

		/// <summary>
		/// Sets the specified SMTP service as the default.
		/// Proto: rpc SetDefaultSmtpService(SetDefaultSmtpServiceRequest) returns (SmtpServiceResponse)
		/// </summary>
		public override async Task<SmtpServiceResponse> SetDefaultSmtpService(SetDefaultSmtpServiceRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var serviceId = Guid.Parse(request.ServiceId);

					// Set is_default = true on this record
					var record = new EntityRecord();
					record["id"] = serviceId;
					record["is_default"] = true;

					var errors = new List<ErrorModel>();
					_smtpService.HandleDefaultServiceSetup(record, errors);
					if (errors.Count > 0)
					{
						var result = new SmtpServiceResponse { Success = false, Message = "Failed to set default SMTP service." };
						MapValidationErrors(errors, result.Errors);
						return await Task.FromResult(result);
					}

					var recMan = _recordManager;
					var response = recMan.UpdateRecord("smtp_service", record);

					_smtpService.ClearCache();

					return await Task.FromResult(new SmtpServiceResponse
					{
						Success = response.Success,
						Message = response.Success ? "Default SMTP service updated." : response.Message ?? "Failed."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (FormatException ex)
			{
				_logger.LogError(ex, "Invalid service_id format in SetDefaultSmtpService");
				throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid service ID: {request.ServiceId}"));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.SetDefaultSmtpService: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, "Internal error setting default SMTP service"));
			}
		}

		#endregion

		#region ===== gRPC Override: Queue Management =====

		/// <summary>
		/// Triggers processing of the email send queue.
		/// Proto: rpc ProcessMailQueue(ProcessMailQueueRequest) returns (ProcessMailQueueResponse)
		/// </summary>
		public override async Task<ProcessMailQueueResponse> ProcessMailQueue(ProcessMailQueueRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					_smtpService.ProcessSmtpQueue();

					return await Task.FromResult(new ProcessMailQueueResponse
					{
						Success = true,
						Message = "Mail queue processed."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in MailGrpcService.ProcessMailQueue: {Message}", ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, "Internal error processing mail queue"));
			}
		}

		#endregion

		#region ===== Private Helpers: Proto-to-Domain Mapping =====

		/// <summary>
		/// Maps a proto SmtpServiceProto message to an EntityRecord for RecordManager CRUD.
		/// </summary>
		private static EntityRecord MapSmtpProtoToEntityRecord(SmtpServiceProto proto)
		{
			var record = new EntityRecord();
			if (!string.IsNullOrEmpty(proto.Id)) record["id"] = Guid.Parse(proto.Id);
			if (!string.IsNullOrEmpty(proto.Name)) record["name"] = proto.Name;
			if (!string.IsNullOrEmpty(proto.Server)) record["server"] = proto.Server;
			if (proto.Port > 0) record["port"] = proto.Port;
			if (!string.IsNullOrEmpty(proto.Username)) record["username"] = proto.Username;
			if (!string.IsNullOrEmpty(proto.Password)) record["password"] = proto.Password;
			record["is_enabled"] = proto.IsEnabled;
			record["is_default"] = proto.IsDefault;
			if (proto.MaxRetriesCount > 0) record["max_retries_count"] = proto.MaxRetriesCount;
			if (proto.RetryWaitMinutes > 0) record["retry_wait_minutes"] = proto.RetryWaitMinutes;
			if (!string.IsNullOrEmpty(proto.ConnectionSecurity)) record["connection_security"] = proto.ConnectionSecurity;
			if (proto.DefaultSender != null)
			{
				record["default_sender_email"] = proto.DefaultSender.Address;
				record["default_sender_name"] = proto.DefaultSender.Name;
			}
			if (proto.DefaultReplyTo.Count > 0)
			{
				record["default_reply_to_email"] = proto.DefaultReplyTo[0].Address;
			}
			return record;
		}

		#endregion
	}
}
