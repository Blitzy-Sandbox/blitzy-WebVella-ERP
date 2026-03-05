using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using WebVella.Erp.Api;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Eql;
using WebVella.Erp.Exceptions;
using WebVella.Erp.Utilities;
using WebVella.Erp.Service.Mail.Domain.Services;
using WebVella.Erp.Service.Mail.Domain.Entities;

namespace WebVella.Erp.Service.Mail.Controllers
{
	/// <summary>
	/// REST API controller exposing mail operations for the Mail/Notification microservice.
	/// Consolidates functionality previously handled through:
	///   1. WebVella.Erp.Web.Controllers.WebApiController — generic record CRUD on email/smtp_service entities
	///   2. WebVella.Erp.Plugins.Mail.Hooks.Api.SmtpServiceRecordHook — SMTP CRUD validation/invariants
	///   3. WebVella.Erp.Plugins.Mail.Hooks.Page.EmailSendNow / TestSmtpService — send and test actions
	///   4. WebVella.Erp.Plugins.Mail.Api.SmtpService — SendEmail/QueueEmail overloads
	///
	/// All 12 monolith business rules are preserved exactly:
	///   1. SMTP service name uniqueness
	///   2. Port range validation 1-65025
	///   3. Email address validation via IsEmail()
	///   4. Max retries range 1-10
	///   5. Retry wait range 1-1440 minutes
	///   6. Connection security MailKit enum validation
	///   7. Default SMTP service singleton invariant
	///   8. Default SMTP service cannot be deleted
	///   9. Email recipient cc:/bcc: prefix handling
	///  10. x_search field preparation on email save
	///  11. Queue processing lock with static lockObject
	///  12. Retry scheduling with configurable wait
	///
	/// JWT Bearer authentication is enforced on all endpoints via [Authorize].
	/// Route pattern preserves backward compatibility: /api/v3/{locale}/...
	/// Response envelope preserves BaseResponseModel contract: {success, errors, timestamp, message, object}
	/// </summary>
	[ApiController]
	[Authorize]
	[Route("api/v3/{locale}")]
	public class MailController : ControllerBase
	{
		private readonly SmtpService _smtpService;

		/// <summary>
		/// Constructs a MailController with the required domain service dependency.
		/// Replaces monolith pattern of <c>new SmtpInternalService()</c> and <c>new EmailServiceManager()</c>.
		/// </summary>
		/// <param name="smtpService">Mail domain service providing all SMTP business logic.</param>
		public MailController(SmtpService smtpService)
		{
			_smtpService = smtpService ?? throw new ArgumentNullException(nameof(smtpService));
		}

		#region <--- Helper Methods (from ApiControllerBase.cs) --->

		/// <summary>
		/// Returns a JSON response with appropriate HTTP status code.
		/// Mirrors the monolith's ApiControllerBase.DoResponse() pattern (lines 16-30).
		/// If the response contains errors or Success is false, sets HTTP 400 (or the
		/// explicitly specified StatusCode).
		/// </summary>
		/// <param name="response">The response model to serialize.</param>
		/// <returns>A JsonResult containing the response model.</returns>
		private IActionResult DoResponse(BaseResponseModel response)
		{
			if (response.Errors.Count > 0 || !response.Success)
			{
				if (response.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)response.StatusCode;
			}
			return new JsonResult(response);
		}

		/// <summary>
		/// Returns a 400 Bad Request response with error details.
		/// Mirrors the monolith's ApiControllerBase.DoBadRequestResponse() pattern (lines 44-62).
		/// Sets Success=false, populates Message from the provided message or exception,
		/// and always returns HTTP 400.
		/// </summary>
		/// <param name="response">The response model to populate with error info.</param>
		/// <param name="message">Optional error message. If null, uses a generic message.</param>
		/// <param name="ex">Optional exception for development-mode error details.</param>
		/// <returns>A JsonResult containing the error response model.</returns>
		private IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			if (ex != null)
			{
				// In development mode, include exception details for debugging.
				// ErpSettings.DevelopmentMode check preserved from monolith ApiControllerBase.
				if (ErpSettings.DevelopmentMode)
					response.Message = ex.Message + ex.StackTrace;
				else
				{
					if (string.IsNullOrEmpty(message))
						response.Message = "An internal error occurred!";
					else
						response.Message = message;
				}
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
				else
					response.Message = message;
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return new JsonResult(response);
		}

		#endregion

		#region <--- Email Endpoints --->

		/// <summary>
		/// POST /api/v3/{locale}/mail/send — Sends an email immediately via SMTP.
		///
		/// Consolidates behavior from:
		///   - SmtpService.SendEmail() (single and multi-recipient overloads)
		///   - EmailSendNow page hook
		///
		/// Request body (JObject):
		///   - recipients: array of {name, address} — required
		///   - subject: string — required
		///   - content_text: string — plain text body
		///   - content_html: string — HTML body
		///   - attachments: array of file path strings — optional
		///   - service_id: guid — optional (uses default SMTP service if null)
		///   - sender: {name, address} — optional sender override
		///
		/// Business rules preserved:
		///   - Recipient validation (null, empty, IsEmail)
		///   - Subject required
		///   - cc:/bcc: prefix handling on recipients
		///   - Attachment path normalization (leading /, lowercase, strip /fs)
		///   - ReplyTo semicolon-delimited support
		/// </summary>
		[HttpPost("mail/send")]
		public async Task<IActionResult> SendEmail([FromRoute] string locale, [FromBody] JObject requestBody)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				if (requestBody == null)
				{
					response.Errors.Add(new ErrorModel("requestBody", "", "Request body is required."));
					return DoResponse(response);
				}

				// Parse recipients
				var recipientsList = new List<EmailAddress>();
				var recipientsToken = requestBody["recipients"];
				if (recipientsToken != null && recipientsToken.Type == JTokenType.Array)
				{
					foreach (var item in (JArray)recipientsToken)
					{
						var name = item.Value<string>("name") ?? string.Empty;
						var address = item.Value<string>("address") ?? string.Empty;
						recipientsList.Add(new EmailAddress(name, address));
					}
				}

				if (recipientsList.Count == 0)
				{
					response.Errors.Add(new ErrorModel("recipients", "", "At least one recipient is required."));
					return DoResponse(response);
				}

				var subject = requestBody.Value<string>("subject") ?? string.Empty;
				var contentText = requestBody.Value<string>("content_text") ?? string.Empty;
				var contentHtml = requestBody.Value<string>("content_html") ?? string.Empty;

				// Parse optional attachments
				var attachments = new List<string>();
				var attachmentsToken = requestBody["attachments"];
				if (attachmentsToken != null && attachmentsToken.Type == JTokenType.Array)
				{
					foreach (var att in (JArray)attachmentsToken)
					{
						var path = att.Value<string>();
						if (!string.IsNullOrWhiteSpace(path))
							attachments.Add(path);
					}
				}

				// Resolve SMTP service (by service_id or default)
				SmtpServiceConfig smtpConfig = null;
				var serviceIdStr = requestBody.Value<string>("service_id");
				if (!string.IsNullOrWhiteSpace(serviceIdStr) && Guid.TryParse(serviceIdStr, out Guid serviceId))
				{
					smtpConfig = _smtpService.GetSmtpService(serviceId);
				}
				else
				{
					// Use default SMTP service (name = null returns default)
					smtpConfig = _smtpService.GetSmtpService(name: null);
				}

				if (smtpConfig == null)
				{
					response.Errors.Add(new ErrorModel("service_id", "", "SMTP service not found."));
					return DoResponse(response);
				}

				// Parse optional sender override
				var senderToken = requestBody["sender"];
				if (senderToken != null && senderToken.Type == JTokenType.Object)
				{
					var senderName = senderToken.Value<string>("name") ?? string.Empty;
					var senderAddress = senderToken.Value<string>("address") ?? string.Empty;
					var sender = new EmailAddress(senderName, senderAddress);

					// Use SendEmail overload with explicit sender
					_smtpService.SendEmail(smtpConfig, recipientsList.First(), sender, subject, contentText, contentHtml, attachments);
				}
				else
				{
					// Use SendEmail with default sender from SMTP config
					if (recipientsList.Count == 1)
					{
						_smtpService.SendEmail(smtpConfig, recipientsList.First(), subject, contentText, contentHtml, attachments);
					}
					else
					{
						_smtpService.SendEmail(smtpConfig, recipientsList, subject, contentText, contentHtml, attachments);
					}
				}

				response.Success = true;
				response.Message = "Email was successfully sent";
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Success = false;
				if (!string.IsNullOrWhiteSpace(valEx.Message))
					response.Message = valEx.Message;
				foreach (var error in valEx.Errors)
				{
					response.Errors.Add(new ErrorModel(error.PropertyName ?? "", "", error.Message));
				}
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// POST /api/v3/{locale}/mail/queue — Queues an email for deferred sending.
		///
		/// Creates an Email entity with Status=Pending and persists via domain service.
		/// The ProcessMailQueueJob background worker will pick up and send queued emails.
		///
		/// Request body (JObject):
		///   - recipients: array of {name, address} — required
		///   - subject: string — required
		///   - content_text: string — plain text body
		///   - content_html: string — HTML body
		///   - priority: int — 0=Low, 1=Normal(default), 2=High
		///   - attachments: array of file path strings — optional
		///   - service_id: guid — optional (uses default if null)
		///   - sender: {name, address} — optional sender override
		///   - reply_to: string — optional semicolon-delimited reply-to addresses
		///
		/// Business rules preserved:
		///   - Recipient cc:/bcc: prefix stripping for validation
		///   - Subject required
		///   - ReplyTo email validation with semicolon splitting
		///   - x_search field preparation on save
		///   - Email status set to Pending, ScheduledOn = UtcNow
		/// </summary>
		[HttpPost("mail/queue")]
		public async Task<IActionResult> QueueEmail([FromRoute] string locale, [FromBody] JObject requestBody)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				if (requestBody == null)
				{
					response.Errors.Add(new ErrorModel("requestBody", "", "Request body is required."));
					return DoResponse(response);
				}

				// Parse recipients
				var recipientsList = new List<EmailAddress>();
				var recipientsToken = requestBody["recipients"];
				if (recipientsToken != null && recipientsToken.Type == JTokenType.Array)
				{
					foreach (var item in (JArray)recipientsToken)
					{
						var name = item.Value<string>("name") ?? string.Empty;
						var address = item.Value<string>("address") ?? string.Empty;
						recipientsList.Add(new EmailAddress(name, address));
					}
				}

				if (recipientsList.Count == 0)
				{
					response.Errors.Add(new ErrorModel("recipients", "", "At least one recipient is required."));
					return DoResponse(response);
				}

				// Validate each recipient — strip cc:/bcc: prefix for validation only
				// Preserves SmtpInternalService.cs lines 623-634 cc:/bcc: prefix handling
				foreach (var recipient in recipientsList)
				{
					var validationAddress = recipient.Address ?? string.Empty;
					if (validationAddress.StartsWith("cc:"))
						validationAddress = validationAddress.Substring(3);
					else if (validationAddress.StartsWith("bcc:"))
						validationAddress = validationAddress.Substring(4);

					if (string.IsNullOrWhiteSpace(validationAddress))
					{
						response.Errors.Add(new ErrorModel("recipientEmail", recipient.Address ?? "", "Recipient email is not specified."));
					}
					else if (!validationAddress.IsEmail())
					{
						response.Errors.Add(new ErrorModel("recipientEmail", recipient.Address ?? "", "Recipient email is not valid email address."));
					}
				}

				var subject = requestBody.Value<string>("subject") ?? string.Empty;
				if (string.IsNullOrWhiteSpace(subject))
				{
					response.Errors.Add(new ErrorModel("subject", "", "Subject is required."));
				}

				// Validate reply_to if provided — semicolon-delimited emails
				// Preserves SmtpInternalService.cs lines 794-802
				var replyTo = requestBody.Value<string>("reply_to") ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(replyTo))
				{
					var replyEmails = replyTo.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
					foreach (var replyEmail in replyEmails)
					{
						if (!replyEmail.Trim().IsEmail())
						{
							response.Errors.Add(new ErrorModel("reply_to", replyEmail.Trim(), "Reply-to email is not a valid email address."));
						}
					}
				}

				if (response.Errors.Any())
				{
					return DoResponse(response);
				}

				var contentText = requestBody.Value<string>("content_text") ?? string.Empty;
				var contentHtml = requestBody.Value<string>("content_html") ?? string.Empty;

				// Parse priority (default: Normal=1)
				var priority = EmailPriority.Normal;
				var priorityStr = requestBody.Value<string>("priority");
				if (!string.IsNullOrWhiteSpace(priorityStr))
				{
					if (Int32.TryParse(priorityStr, out int priorityInt) && Enum.IsDefined(typeof(EmailPriority), priorityInt))
					{
						priority = (EmailPriority)priorityInt;
					}
				}

				// Parse optional attachments
				var attachments = new List<string>();
				var attachmentsToken = requestBody["attachments"];
				if (attachmentsToken != null && attachmentsToken.Type == JTokenType.Array)
				{
					foreach (var att in (JArray)attachmentsToken)
					{
						var path = att.Value<string>();
						if (!string.IsNullOrWhiteSpace(path))
							attachments.Add(path);
					}
				}

				// Resolve SMTP service for ServiceId
				SmtpServiceConfig smtpConfig = null;
				var serviceIdStr = requestBody.Value<string>("service_id");
				if (!string.IsNullOrWhiteSpace(serviceIdStr) && Guid.TryParse(serviceIdStr, out Guid serviceId))
				{
					smtpConfig = _smtpService.GetSmtpService(serviceId);
				}
				else
				{
					smtpConfig = _smtpService.GetSmtpService(name: null);
				}

				if (smtpConfig == null)
				{
					response.Errors.Add(new ErrorModel("service_id", "", "SMTP service not found."));
					return DoResponse(response);
				}

				// Parse optional sender override
				EmailAddress sender = null;
				var senderToken = requestBody["sender"];
				if (senderToken != null && senderToken.Type == JTokenType.Object)
				{
					var senderName = senderToken.Value<string>("name") ?? string.Empty;
					var senderAddress = senderToken.Value<string>("address") ?? string.Empty;
					sender = new EmailAddress(senderName, senderAddress);
				}
				else
				{
					sender = new EmailAddress
					{
						Address = smtpConfig.DefaultSenderEmail,
						Name = smtpConfig.DefaultSenderName
					};
				}

				// Create Email entity with Pending status — preserves SmtpService.QueueEmail pattern
				var email = new Email();
				email.Id = Guid.NewGuid();
				email.ServiceId = smtpConfig.Id;
				email.Sender = sender;
				email.Recipients = recipientsList;
				email.ReplyToEmail = !string.IsNullOrWhiteSpace(replyTo) ? replyTo : smtpConfig.DefaultReplyToEmail;
				email.Subject = subject;
				email.ContentText = contentText;
				email.ContentHtml = contentHtml;
				email.CreatedOn = DateTime.UtcNow;
				email.SentOn = null;
				email.Status = EmailStatus.Pending;
				email.Priority = priority;
				email.ServerError = string.Empty;
				email.ScheduledOn = DateTime.UtcNow;
				email.RetriesCount = 0;
				email.Attachments = attachments;

				// Persist via domain service — calls PrepareEmailXSearch internally
				_smtpService.SaveEmail(email);

				response.Success = true;
				response.Message = "Email was successfully queued";
				response.Object = email;
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Success = false;
				if (!string.IsNullOrWhiteSpace(valEx.Message))
					response.Message = valEx.Message;
				foreach (var error in valEx.Errors)
				{
					response.Errors.Add(new ErrorModel(error.PropertyName ?? "", "", error.Message));
				}
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// GET /api/v3/{locale}/mail/emails — Lists emails with pagination and optional filtering.
		///
		/// Uses EQL query engine for database access, preserving the monolith's
		/// EqlCommand pattern from WebApiController.
		///
		/// Query parameters:
		///   - page: int (default 1) — page number for pagination
		///   - pageSize: int (default 10) — records per page
		///   - status: string — optional filter by email status
		///   - sortBy: string (default "created_on") — field to sort by
		///   - sortOrder: string (default "desc") — sort direction (asc/desc)
		/// </summary>
		[HttpGet("mail/emails")]
		public IActionResult ListEmails(
			[FromRoute] string locale,
			[FromQuery] int page = 1,
			[FromQuery] int pageSize = 10,
			[FromQuery] string status = null,
			[FromQuery] string sortBy = "created_on",
			[FromQuery] string sortOrder = "desc")
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				// Sanitize sort parameters to prevent injection
				var allowedSortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				{
					"created_on", "sent_on", "subject", "status", "priority", "scheduled_on", "id"
				};
				if (!allowedSortFields.Contains(sortBy))
					sortBy = "created_on";

				var sanitizedSortOrder = sortOrder?.ToLowerInvariant() == "asc" ? "ASC" : "DESC";

				// Build EQL query with optional status filter
				var parameters = new List<EqlParameter>();
				string eql;

				if (!string.IsNullOrWhiteSpace(status))
				{
					eql = $"SELECT * FROM email WHERE status = @status ORDER BY {sortBy} {sanitizedSortOrder} PAGE @page PAGESIZE @pageSize";
					parameters.Add(new EqlParameter("status", status));
				}
				else
				{
					eql = $"SELECT * FROM email ORDER BY {sortBy} {sanitizedSortOrder} PAGE @page PAGESIZE @pageSize";
				}

				parameters.Add(new EqlParameter("page", page));
				parameters.Add(new EqlParameter("pageSize", pageSize));

				var result = new EqlCommand(eql, parameters.ToArray()).Execute();

				response.Success = true;
				response.Object = new { list = result, total_count = result.TotalCount };
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// GET /api/v3/{locale}/mail/emails/{id} — Gets a single email by its unique identifier.
		///
		/// Uses EQL query: SELECT * FROM email WHERE id = @id
		/// Mirrors SmtpInternalService.GetEmail() (lines 674-681).
		/// </summary>
		[HttpGet("mail/emails/{id:guid}")]
		public IActionResult GetEmail([FromRoute] string locale, [FromRoute] Guid id)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				var result = new EqlCommand(
					"SELECT * FROM email WHERE id = @id",
					new EqlParameter("id", id)
				).Execute();

				if (result.Count == 0)
				{
					response.Success = false;
					response.Message = "Email not found";
					HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
					return new JsonResult(response);
				}

				response.Success = true;
				response.Object = result[0];
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		#endregion

		#region <--- SMTP Service Endpoints --->

		/// <summary>
		/// GET /api/v3/{locale}/mail/smtp-services — Lists all SMTP service configurations.
		///
		/// Uses EQL query: SELECT * FROM smtp_service
		/// Domain service caching (1-hour TTL via Redis) is applied at the service layer
		/// per the EmailServiceManager pattern.
		/// </summary>
		[HttpGet("mail/smtp-services")]
		public IActionResult ListSmtpServices([FromRoute] string locale)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				var result = new EqlCommand("SELECT * FROM smtp_service").Execute();

				response.Success = true;
				response.Object = result;
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// POST /api/v3/{locale}/mail/smtp-services — Creates a new SMTP service configuration.
		///
		/// Consolidates validation logic from:
		///   - SmtpServiceRecordHook.OnPreCreateRecord (lines 17-23)
		///   - SmtpInternalService.ValidatePreCreateRecord (lines 33-188)
		///   - SmtpInternalService.HandleDefaultServiceSetup (lines 356-385)
		///
		/// Business rules preserved:
		///   1. Name uniqueness — EQL check, error if count > 0
		///   2. Port range 1-65025
		///   3. default_from_email must be valid email
		///   4. default_reply_to_email must be valid email if not empty
		///   5. max_retries_count range 1-10
		///   6. retry_wait_minutes range 1-1440
		///   7. connection_security MailKit SecureSocketOptions validation
		///   8. Default service singleton — if is_default=true, clear all others
		/// </summary>
		[HttpPost("mail/smtp-services")]
		public IActionResult CreateSmtpService([FromRoute] string locale, [FromBody] JObject requestBody)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				if (requestBody == null)
				{
					response.Errors.Add(new ErrorModel("requestBody", "", "Request body is required."));
					return DoResponse(response);
				}

				// Build EntityRecord from JObject — mirrors WebApiController record construction
				var record = new EntityRecord();
				record["id"] = Guid.NewGuid();
				foreach (var prop in requestBody.Properties())
				{
					var key = prop.Name.ToLowerInvariant();
					switch (key)
					{
						case "name":
							record["name"] = prop.Value.ToString();
							break;
						case "server":
							record["server"] = prop.Value.ToString();
							break;
						case "port":
							record["port"] = prop.Value.ToString();
							break;
						case "username":
							record["username"] = prop.Value.ToString();
							break;
						case "password":
							record["password"] = prop.Value.ToString();
							break;
						case "default_sender_name":
							record["default_sender_name"] = prop.Value.ToString();
							break;
						case "default_sender_email":
						case "default_from_email":
							record["default_from_email"] = prop.Value.ToString();
							break;
						case "default_reply_to_email":
							record["default_reply_to_email"] = prop.Value.ToString();
							break;
						case "max_retries_count":
							record["max_retries_count"] = prop.Value.ToString();
							break;
						case "retry_wait_minutes":
							record["retry_wait_minutes"] = prop.Value.ToString();
							break;
						case "is_default":
							record["is_default"] = prop.Value.ToObject<bool>();
							break;
						case "is_enabled":
							record["is_enabled"] = prop.Value.ToObject<bool>();
							break;
						case "connection_security":
							record["connection_security"] = prop.Value.ToString();
							break;
					}
				}

				// Run pre-create validations — mirrors SmtpServiceRecordHook.OnPreCreateRecord
				var errors = new List<ErrorModel>();
				_smtpService.ValidatePreCreateRecord(record, errors);
				if (errors.Any())
				{
					response.Errors.AddRange(errors);
					return DoResponse(response);
				}

				// Handle default service setup — mirrors SmtpServiceRecordHook.OnPreCreateRecord
				_smtpService.HandleDefaultServiceSetup(record, errors);
				if (errors.Any())
				{
					response.Errors.AddRange(errors);
					return DoResponse(response);
				}

				// Create record via RecordManager — mirrors monolith CRUD pattern
				var recMan = new RecordManager();
				var createResponse = recMan.CreateRecord("smtp_service", record);
				if (!createResponse.Success)
				{
					response.Errors.Add(new ErrorModel("", "", createResponse.Message));
					return DoResponse(response);
				}

				// Clear SMTP service cache — mirrors SmtpServiceRecordHook.OnPostCreateRecord
				_smtpService.ClearCache();

				response.Success = true;
				response.Message = "SMTP service created successfully";
				response.Object = record;
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Success = false;
				if (!string.IsNullOrWhiteSpace(valEx.Message))
					response.Message = valEx.Message;
				foreach (var error in valEx.Errors)
				{
					response.Errors.Add(new ErrorModel(error.PropertyName ?? "", "", error.Message));
				}
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// PUT /api/v3/{locale}/mail/smtp-services/{id} — Updates an existing SMTP service configuration.
		///
		/// Consolidates validation logic from:
		///   - SmtpServiceRecordHook.OnPreUpdateRecord (lines 25-30)
		///   - SmtpInternalService.ValidatePreUpdateRecord (lines 190-354)
		///   - SmtpInternalService.HandleDefaultServiceSetup (lines 356-385)
		///
		/// Business rules preserved (same as create plus):
		///   - Name uniqueness allows same record (count > 1 || count == 1 && id != record.id)
		///   - Unsetting is_default on current default is forbidden
		/// </summary>
		[HttpPut("mail/smtp-services/{id:guid}")]
		public IActionResult UpdateSmtpService([FromRoute] string locale, [FromRoute] Guid id, [FromBody] JObject requestBody)
		{
			var response = new ResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				if (requestBody == null)
				{
					response.Errors.Add(new ErrorModel("requestBody", "", "Request body is required."));
					return DoResponse(response);
				}

				// Build EntityRecord from JObject — set id from route parameter
				var record = new EntityRecord();
				record["id"] = id;
				foreach (var prop in requestBody.Properties())
				{
					var key = prop.Name.ToLowerInvariant();
					switch (key)
					{
						case "name":
							record["name"] = prop.Value.ToString();
							break;
						case "server":
							record["server"] = prop.Value.ToString();
							break;
						case "port":
							record["port"] = prop.Value.ToString();
							break;
						case "username":
							record["username"] = prop.Value.ToString();
							break;
						case "password":
							record["password"] = prop.Value.ToString();
							break;
						case "default_sender_name":
							record["default_sender_name"] = prop.Value.ToString();
							break;
						case "default_sender_email":
						case "default_from_email":
							record["default_from_email"] = prop.Value.ToString();
							break;
						case "default_reply_to_email":
							record["default_reply_to_email"] = prop.Value.ToString();
							break;
						case "max_retries_count":
							record["max_retries_count"] = prop.Value.ToString();
							break;
						case "retry_wait_minutes":
							record["retry_wait_minutes"] = prop.Value.ToString();
							break;
						case "is_default":
							record["is_default"] = prop.Value.ToObject<bool>();
							break;
						case "is_enabled":
							record["is_enabled"] = prop.Value.ToObject<bool>();
							break;
						case "connection_security":
							record["connection_security"] = prop.Value.ToString();
							break;
					}
				}

				// Run pre-update validations — mirrors SmtpServiceRecordHook.OnPreUpdateRecord
				var errors = new List<ErrorModel>();
				_smtpService.ValidatePreUpdateRecord(record, errors);
				if (errors.Any())
				{
					response.Errors.AddRange(errors);
					return DoResponse(response);
				}

				// Handle default service setup — mirrors SmtpServiceRecordHook.OnPreUpdateRecord
				_smtpService.HandleDefaultServiceSetup(record, errors);
				if (errors.Any())
				{
					response.Errors.AddRange(errors);
					return DoResponse(response);
				}

				// Update record via RecordManager — mirrors monolith CRUD pattern
				var recMan = new RecordManager();
				var updateResponse = recMan.UpdateRecord("smtp_service", record);
				if (!updateResponse.Success)
				{
					response.Errors.Add(new ErrorModel("", "", updateResponse.Message));
					return DoResponse(response);
				}

				// Clear SMTP service cache — mirrors SmtpServiceRecordHook.OnPostUpdateRecord
				_smtpService.ClearCache();

				response.Success = true;
				response.Message = "SMTP service updated successfully";
				response.Object = record;
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Success = false;
				if (!string.IsNullOrWhiteSpace(valEx.Message))
					response.Message = valEx.Message;
				foreach (var error in valEx.Errors)
				{
					response.Errors.Add(new ErrorModel(error.PropertyName ?? "", "", error.Message));
				}
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// DELETE /api/v3/{locale}/mail/smtp-services/{id} — Deletes an SMTP service configuration.
		///
		/// Preserves critical business rule from SmtpServiceRecordHook.OnPreDeleteRecord (lines 43-49):
		///   CRITICAL: Default SMTP service cannot be deleted.
		///   The system must always have an active default SMTP service.
		/// </summary>
		[HttpDelete("mail/smtp-services/{id:guid}")]
		public IActionResult DeleteSmtpService([FromRoute] string locale, [FromRoute] Guid id)
		{
			var response = new BaseResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				// Load SMTP service to check deletion eligibility
				SmtpServiceConfig service = null;
				try
				{
					service = _smtpService.GetSmtpService(id);
				}
				catch
				{
					// Service not found — allow deletion attempt (RecordManager will handle)
				}

				// CRITICAL BUSINESS RULE: Default smtp service cannot be deleted.
				// Preserves SmtpServiceRecordHook.OnPreDeleteRecord line 47
				if (service != null && service.IsDefault)
				{
					response.Errors.Add(new ErrorModel { Key = "id", Message = "Default smtp service cannot be deleted." });
					return DoResponse(response);
				}

				// Delete record via RecordManager — mirrors monolith CRUD pattern
				// DeleteRecord(string entityName, Guid id) — direct Guid overload
				var recMan = new RecordManager();
				var deleteResponse = recMan.DeleteRecord("smtp_service", id);
				if (!deleteResponse.Success)
				{
					response.Errors.Add(new ErrorModel("", "", deleteResponse.Message));
					return DoResponse(response);
				}

				// Clear SMTP service cache — mirrors SmtpServiceRecordHook.OnPreDeleteRecord line 49
				_smtpService.ClearCache();

				response.Success = true;
				response.Message = "SMTP service deleted successfully";
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		/// <summary>
		/// POST /api/v3/{locale}/mail/smtp-services/{id}/test — Tests SMTP service connectivity.
		///
		/// Sends a test email through the specified SMTP service to verify configuration.
		/// Preserves behavior from SmtpInternalService.TestSmtpServiceOnPost() (lines 387-478).
		///
		/// Request body (JObject):
		///   - recipient_email: string — required, must be valid email
		///   - subject: string — required
		///   - content: string — required (HTML content)
		///   - attachments: string — optional, comma-separated file IDs
		///
		/// Business rules preserved:
		///   - All three fields required with validation
		///   - SMTP service must exist
		///   - Attachment file resolution via EQL (user_file entity)
		///   - SendEmail called with resolved config
		/// </summary>
		[HttpPost("mail/smtp-services/{id:guid}/test")]
		public IActionResult TestSmtpService([FromRoute] string locale, [FromRoute] Guid id, [FromBody] JObject requestBody)
		{
			var response = new BaseResponseModel();
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			try
			{
				if (requestBody == null)
				{
					response.Errors.Add(new ErrorModel("requestBody", "", "Request body is required."));
					return DoResponse(response);
				}

				SmtpServiceConfig smtpConfig = null;
				string recipientEmail = string.Empty;
				string subject = string.Empty;
				string content = string.Empty;

				// Validate recipient_email — preserves TestSmtpServiceOnPost lines 403-412
				var recipientEmailValue = requestBody.Value<string>("recipient_email");
				if (string.IsNullOrWhiteSpace(recipientEmailValue))
				{
					response.Errors.Add(new ErrorModel("recipient_email", "", "Recipient email is not specified."));
				}
				else
				{
					recipientEmail = recipientEmailValue;
					if (!recipientEmail.IsEmail())
					{
						response.Errors.Add(new ErrorModel("recipient_email", recipientEmail, "Recipient email is not a valid email address"));
					}
				}

				// Validate subject — preserves TestSmtpServiceOnPost lines 414-421
				var subjectValue = requestBody.Value<string>("subject");
				if (string.IsNullOrWhiteSpace(subjectValue))
				{
					response.Errors.Add(new ErrorModel("subject", "", "Subject is required"));
				}
				else
				{
					subject = subjectValue;
				}

				// Validate content — preserves TestSmtpServiceOnPost lines 423-430
				var contentValue = requestBody.Value<string>("content");
				if (string.IsNullOrWhiteSpace(contentValue))
				{
					response.Errors.Add(new ErrorModel("content", "", "Content is required"));
				}
				else
				{
					content = contentValue;
				}

				// Resolve SMTP service by id — preserves TestSmtpServiceOnPost lines 432-441
				try
				{
					smtpConfig = _smtpService.GetSmtpService(id);
				}
				catch
				{
					smtpConfig = null;
				}

				if (smtpConfig == null)
				{
					response.Errors.Add(new ErrorModel("serviceId", id.ToString(), "Smtp service with specified id does not exist"));
				}

				// Resolve attachment file paths if provided — preserves TestSmtpServiceOnPost lines 443-453
				var attachments = new List<string>();
				var attachmentsValue = requestBody.Value<string>("attachments");
				if (!string.IsNullOrWhiteSpace(attachmentsValue))
				{
					var ids = attachmentsValue.Split(",", StringSplitOptions.RemoveEmptyEntries);
					foreach (var fileIdStr in ids)
					{
						if (Guid.TryParse(fileIdStr.Trim(), out Guid fileId))
						{
							try
							{
								var fileRecord = new EqlCommand(
									"SELECT name,path FROM user_file WHERE id = @id",
									new EqlParameter("id", fileId)
								).Execute().FirstOrDefault();
								if (fileRecord != null)
								{
									attachments.Add((string)fileRecord["path"]);
								}
							}
							catch
							{
								// File not found — skip silently, matching monolith behavior
							}
						}
					}
				}

				// Return errors if any validation failed
				if (response.Errors.Any())
				{
					return DoResponse(response);
				}

				// Send test email — preserves TestSmtpServiceOnPost lines 464-467
				var recipient = new EmailAddress(recipientEmail);
				_smtpService.SendEmail(smtpConfig, recipient, subject, string.Empty, content, attachments: attachments);

				response.Success = true;
				response.Message = "Email was successfully sent";
				return DoResponse(response);
			}
			catch (ValidationException valEx)
			{
				response.Success = false;
				if (!string.IsNullOrWhiteSpace(valEx.Message))
					response.Message = valEx.Message;
				foreach (var error in valEx.Errors)
				{
					response.Errors.Add(new ErrorModel(error.PropertyName ?? "", "", error.Message));
				}
				return DoResponse(response);
			}
			catch (Exception ex)
			{
				return DoBadRequestResponse(response, ex.Message, ex);
			}
		}

		#endregion
	}
}
