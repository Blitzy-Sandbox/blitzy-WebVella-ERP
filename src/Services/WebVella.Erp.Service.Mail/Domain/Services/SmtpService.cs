using HtmlAgilityPack;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Caching.Distributed;
using MimeKit;
using MimeKit.Utils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WebVella.Erp.Api;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Api.Models.AutoMapper;
using WebVella.Erp.Database;
using WebVella.Erp.Eql;
using WebVella.Erp.Exceptions;
using WebVella.Erp.Utilities;
using WebVella.Erp.Service.Mail.Domain.Entities;

namespace WebVella.Erp.Service.Mail.Domain.Services
{
	/// <summary>
	/// Core domain service for the Mail/Notification microservice.
	/// Consolidates ALL SMTP business logic from three monolith source files:
	///   1. WebVella.Erp.Plugins.Mail.Services.SmtpInternalService (validation, HTML processing, queue)
	///   2. WebVella.Erp.Plugins.Mail.Api.SmtpService (SendEmail/QueueEmail overloads)
	///   3. WebVella.Erp.Plugins.Mail.Api.EmailServiceManager (SMTP config caching)
	///
	/// Preserves 100% of existing business logic with the following adaptations:
	///   - IMemoryCache replaced with IDistributedCache (Redis) for SMTP config caching
	///   - SmtpService config model renamed to SmtpServiceConfig to avoid naming collision
	///   - SendEmail/QueueEmail methods take SmtpServiceConfig parameter instead of using 'this'
	///   - new SmtpInternalService() calls replaced with direct method calls (same class)
	///   - new EmailServiceManager() calls replaced with direct method calls (same class)
	///   - UI-specific methods (TestSmtpServiceOnPost, EmailSendNowOnPost) excluded
	/// </summary>
	public class SmtpService
	{
		private readonly IDistributedCache _cache;

		private static readonly object lockObject = new object();
		private static bool queueProcessingInProgress = false;

		/// <summary>
		/// Cache TTL for SMTP service configurations (1 hour).
		/// Preserves monolith's IMemoryCache 1-hour absolute expiration per AAP 0.8.3.
		/// </summary>
		private static readonly DistributedCacheEntryOptions CacheOptions = new DistributedCacheEntryOptions
		{
			AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
		};

		/// <summary>
		/// Constructs a new SmtpService with distributed cache dependency.
		/// </summary>
		/// <param name="cache">IDistributedCache implementation (Redis) for SMTP config caching.</param>
		public SmtpService(IDistributedCache cache)
		{
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		}

		#region <--- SMTP Service Caching (from EmailServiceManager.cs) --->

		/// <summary>
		/// Gets an SMTP service configuration by its unique identifier.
		/// Uses distributed Redis cache with 1-hour TTL.
		/// Source: EmailServiceManager.cs lines 53-64
		/// </summary>
		public SmtpServiceConfig GetSmtpService(Guid id)
		{
			string cacheKey = $"SMTP-{id}";
			SmtpServiceConfig service = null;

			var cached = _cache.GetString(cacheKey);
			if (cached != null)
			{
				service = JsonConvert.DeserializeObject<SmtpServiceConfig>(cached);
			}

			if (service == null)
			{
				service = GetSmtpServiceInternal(id);
				if (service != null)
				{
					_cache.SetString(cacheKey, JsonConvert.SerializeObject(service), CacheOptions);
				}
			}
			return service;
		}

		/// <summary>
		/// Gets an SMTP service configuration by name, or the default service if name is null.
		/// Uses distributed Redis cache with 1-hour TTL.
		/// Source: EmailServiceManager.cs lines 66-77
		/// </summary>
		public SmtpServiceConfig GetSmtpService(string name = null)
		{
			string cacheKey = $"SMTP-{name}";
			SmtpServiceConfig service = null;

			var cached = _cache.GetString(cacheKey);
			if (cached != null)
			{
				service = JsonConvert.DeserializeObject<SmtpServiceConfig>(cached);
			}

			if (service == null)
			{
				service = GetSmtpServiceInternal(name);
				if (service != null)
				{
					_cache.SetString(cacheKey, JsonConvert.SerializeObject(service), CacheOptions);
				}
			}
			return service;
		}

		/// <summary>
		/// Internal method to query SMTP service by name from database.
		/// When name is null, returns the default service.
		/// Source: EmailServiceManager.cs lines 79-101
		/// </summary>
		public SmtpServiceConfig GetSmtpServiceInternal(string name = null)
		{
			EntityRecord smtpServiceRec = null;
			if (name != null)
			{
				var result = new EqlCommand("SELECT * FROM smtp_service WHERE name = @name", new EqlParameter("name", name)).Execute();
				if (result.Count == 0)
					throw new Exception($"SmtpService with name '{name}' not found.");

				smtpServiceRec = result[0];
			}
			else
			{
				var result = new EqlCommand("SELECT * FROM smtp_service WHERE is_default = @is_default", new EqlParameter("is_default", true)).Execute();
				if (result.Count == 0)
					throw new Exception($"Default SmtpService not found.");
				else if (result.Count > 1)
					throw new Exception($"More than one default SmtpService not found.");

				smtpServiceRec = result[0];
			}
			return smtpServiceRec.MapTo<SmtpServiceConfig>();
		}

		/// <summary>
		/// Internal method to query SMTP service by id from database.
		/// Source: EmailServiceManager.cs lines 103-110
		/// </summary>
		public SmtpServiceConfig GetSmtpServiceInternal(Guid id)
		{
			var result = new EqlCommand("SELECT * FROM smtp_service WHERE id = @id", new EqlParameter("id", id)).Execute();
			if (result.Count == 0)
				throw new Exception($"SmtpService with id = '{id}' not found.");

			return result[0].MapTo<SmtpServiceConfig>();
		}

		/// <summary>
		/// Clears all cached SMTP service configurations.
		/// Since IDistributedCache cannot enumerate keys, this removes commonly-known cache entries.
		/// New entries will be re-cached on next access with 1-hour TTL.
		/// </summary>
		public void ClearCache()
		{
			try
			{
				// Query all SMTP service records to get their ids and names for targeted cache removal
				var result = new EqlCommand("SELECT id,name FROM smtp_service").Execute();
				foreach (var record in result)
				{
					var id = record["id"];
					var svcName = record["name"] as string;
					if (id != null)
						_cache.Remove($"SMTP-{id}");
					if (svcName != null)
						_cache.Remove($"SMTP-{svcName}");
				}
				// Also remove the default service cache key (name = null)
				_cache.Remove("SMTP-");
			}
			catch
			{
				// Cache clearing is best-effort; entries will expire naturally via TTL
			}
		}

		#endregion

		#region <--- Hooks Logic / Validation (from SmtpInternalService.cs) --->

		/// <summary>
		/// Validates SMTP service record fields before creation.
		/// Preserves ALL validation rules, error messages, and conditional branches exactly.
		/// Source: SmtpInternalService.cs lines 33-188
		/// </summary>
		public void ValidatePreCreateRecord(EntityRecord rec, List<ErrorModel> errors)
		{
			foreach (var prop in rec.Properties)
			{
				switch (prop.Key)
				{
					case "name":
						{
							var result = new EqlCommand("SELECT * FROM smtp_service WHERE name = @name", new EqlParameter("name", rec["name"])).Execute();
							if (result.Count > 0)
							{
								errors.Add(new ErrorModel
								{
									Key = "name",
									Value = (string)rec["name"],
									Message = "There is already existing service with that name. Name must be unique"
								});
							}
						}
						break;
					case "port":
						{
							if (!Int32.TryParse(rec["port"]?.ToString(), out int port))
							{
								errors.Add(new ErrorModel
								{
									Key = "port",
									Value = rec["port"]?.ToString(),
									Message = $"Port must be an integer value between 1 and 65025"
								});
							}
							else
							{
								if (port <= 0 || port > 65025)
								{
									errors.Add(new ErrorModel
									{
										Key = "port",
										Value = rec["port"]?.ToString(),
										Message = $"Port must be an integer value between 1 and 65025"
									});
								}
							}

						}
						break;
					case "default_from_email":
						{
							if (!((string)rec["default_from_email"]).IsEmail())
							{
								errors.Add(new ErrorModel
								{
									Key = "default_from_email",
									Value = (string)rec["default_from_email"],
									Message = $"Default from email address is invalid"
								});
							}
						}
						break;
					case "default_reply_to_email":
						{
							if (string.IsNullOrWhiteSpace((string)rec["default_reply_to_email"]))
								continue;

							if (!((string)rec["default_reply_to_email"]).IsEmail())
							{
								errors.Add(new ErrorModel
								{
									Key = "default_reply_to_email",
									Value = (string)rec["default_reply_to_email"],
									Message = $"Default reply to email address is invalid"
								});
							}
						}
						break;
					case "max_retries_count":
						{
							if (!Int32.TryParse(rec["max_retries_count"]?.ToString(), out int count))
							{
								errors.Add(new ErrorModel
								{
									Key = "max_retries_count",
									Value = rec["max_retries_count"]?.ToString(),
									Message = $"Number of retries on error must be an integer value between 1 and 10"
								});
							}
							else
							{
								if (count < 1 || count > 10)
								{
									errors.Add(new ErrorModel
									{
										Key = "max_retries_count",
										Value = rec["max_retries_count"]?.ToString(),
										Message = $"Number of retries on error must be an integer value between 1 and 10"
									});
								}
							}
						}
						break;
					case "retry_wait_minutes":
						{
							if (!Int32.TryParse(rec["retry_wait_minutes"]?.ToString(), out int minutes))
							{
								errors.Add(new ErrorModel
								{
									Key = "retry_wait_minutes",
									Value = rec["retry_wait_minutes"]?.ToString(),
									Message = $"Wait period between retries must be an integer value between 1 and 1440 minutes"
								});
							}
							else
							{
								if (minutes < 1 || minutes > 1440)
								{
									errors.Add(new ErrorModel
									{
										Key = "retry_wait_minutes",
										Value = rec["retry_wait_minutes"]?.ToString(),
										Message = $"Wait period between retries must be an integer value between 1 and 1440 minutes"
									});
								}
							}
						}
						break;
					case "connection_security":
						{
							if (!Int32.TryParse(rec["connection_security"] as string, out int connectionSecurityNumber))
							{
								errors.Add(new ErrorModel
								{
									Key = "connection_security",
									Value = (string)rec["connection_security"],
									Message = $"Invalid connection security setting selected."
								});
								continue;
							}

							try
							{
								var secOptions = (SecureSocketOptions)connectionSecurityNumber;
							}
							catch
							{
								errors.Add(new ErrorModel
								{
									Key = "connection_security",
									Value = (string)rec["connection_security"],
									Message = $"Invalid connection security setting selected."
								});
							}
						}
						break;
				}
			}
		}

		/// <summary>
		/// Validates SMTP service record fields before update.
		/// Nearly identical to Create validation with key differences:
		///   - name: allows count == 1 if same id (existing record being updated)
		///   - port: uses 'rec["port"] as string' (not ?.ToString())
		///   - max_retries_count: uses 'rec["max_retries_count"] as string' (not ?.ToString())
		/// Source: SmtpInternalService.cs lines 190-354
		/// </summary>
		public void ValidatePreUpdateRecord(EntityRecord rec, List<ErrorModel> errors)
		{
			foreach (var prop in rec.Properties)
			{
				switch (prop.Key)
				{
					case "name":
						{
							var result = new EqlCommand("SELECT * FROM smtp_service WHERE name = @name", new EqlParameter("name", rec["name"])).Execute();
							if (result.Count > 1)
							{
								errors.Add(new ErrorModel
								{
									Key = "name",
									Value = (string)rec["name"],
									Message = "There is already existing service with that name. Name must be unique"
								});
							}
							else if (result.Count == 1 && (Guid)result[0]["id"] != (Guid)rec["id"])
							{
								errors.Add(new ErrorModel
								{
									Key = "name",
									Value = (string)rec["name"],
									Message = "There is already existing service with that name. Name must be unique"
								});
							}
						}
						break;
					case "port":
						{
							if (!Int32.TryParse(rec["port"] as string, out int port))
							{
								errors.Add(new ErrorModel
								{
									Key = "port",
									Value = (string)rec["port"],
									Message = $"Port must be an integer value between 1 and 65025"
								});
							}
							else
							{
								if (port <= 0 || port > 65025)
								{
									errors.Add(new ErrorModel
									{
										Key = "port",
										Value = (string)rec["port"],
										Message = $"Port must be an integer value between 1 and 65025"
									});
								}
							}

						}
						break;
					case "default_from_email":
						{
							if (!((string)rec["default_from_email"]).IsEmail())
							{
								errors.Add(new ErrorModel
								{
									Key = "default_from_email",
									Value = (string)rec["default_from_email"],
									Message = $"Default from email address is invalid"
								});
							}
						}
						break;
					case "default_reply_to_email":
						{
							if (string.IsNullOrWhiteSpace((string)rec["default_reply_to_email"]))
								continue;

							if (!((string)rec["default_reply_to_email"]).IsEmail())
							{
								errors.Add(new ErrorModel
								{
									Key = "default_reply_to_email",
									Value = (string)rec["default_reply_to_email"],
									Message = $"Default reply to email address is invalid"
								});
							}
						}
						break;
					case "max_retries_count":
						{
							if (!Int32.TryParse(rec["max_retries_count"] as string, out int count))
							{
								errors.Add(new ErrorModel
								{
									Key = "max_retries_count",
									Value = (string)rec["max_retries_count"],
									Message = $"Number of retries on error must be an integer value between 1 and 10"
								});
							}
							else
							{
								if (count < 1 || count > 10)
								{
									errors.Add(new ErrorModel
									{
										Key = "max_retries_count",
										Value = (string)rec["max_retries_count"],
										Message = $"Number of retries on error must be an integer value between 1 and 10"
									});
								}
							}
						}
						break;
					case "retry_wait_minutes":
						{
							if (!Int32.TryParse(rec["retry_wait_minutes"] as string, out int minutes))
							{
								errors.Add(new ErrorModel
								{
									Key = "retry_wait_minutes",
									Value = (string)rec["retry_wait_minutes"],
									Message = $"Wait period between retries must be an integer value between 1 and 1440 minutes"
								});
							}
							else
							{
								if (minutes < 1 || minutes > 1440)
								{
									errors.Add(new ErrorModel
									{
										Key = "retry_wait_minutes",
										Value = (string)rec["retry_wait_minutes"],
										Message = $"Wait period between retries must be an integer value between 1 and 1440 minutes"
									});
								}
							}
						}
						break;
					case "connection_security":
						{
							if (!Int32.TryParse(rec["connection_security"] as string, out int connectionSecurityNumber))
							{
								errors.Add(new ErrorModel
								{
									Key = "connection_security",
									Value = (string)rec["connection_security"],
									Message = $"Invalid connection security setting selected."
								});
								continue;
							}

							try
							{
								var secOptions = (SecureSocketOptions)connectionSecurityNumber;
							}
							catch
							{
								errors.Add(new ErrorModel
								{
									Key = "connection_security",
									Value = (string)rec["connection_security"],
									Message = $"Invalid connection security setting selected."
								});
							}
						}
						break;
				}
			}
		}

		/// <summary>
		/// Handles default SMTP service setup — ensures exactly one default service exists.
		/// When setting is_default=true: clears is_default on all other services.
		/// When setting is_default=false: blocks if this record IS the current default.
		/// Source: SmtpInternalService.cs lines 356-385
		/// </summary>
		public void HandleDefaultServiceSetup(EntityRecord rec, List<ErrorModel> errors)
		{
			if (rec.Properties.ContainsKey("is_default") && (bool)rec["is_default"])
			{

				var recMan = new RecordManager(executeHooks: false);
				var records = new EqlCommand("SELECT id,is_default FROM smtp_service").Execute();
				foreach (var record in records)
				{
					if ((bool)record["is_default"])
					{
						record["is_default"] = false;
						recMan.UpdateRecord("smtp_service", record);
					}
				}
			}
			else if (rec.Properties.ContainsKey("is_default") && (bool)rec["is_default"] == false)
			{
				var currentRecord = new EqlCommand("SELECT * FROM smtp_service WHERE id = @id", new EqlParameter("id", rec["id"])).Execute();
				if (currentRecord.Count > 0 && (bool)currentRecord[0]["is_default"])
				{
					errors.Add(new ErrorModel
					{
						Key = "is_default",
						Value = ((bool)rec["is_default"]).ToString(),
						Message = $"Forbidden. There should always be an active default service."
					});
				}
			}
		}

		#endregion

		#region <--- Email Persistence (from SmtpInternalService.cs) --->

		/// <summary>
		/// Saves an email record (creates or updates).
		/// Prepares the x_search field before persisting.
		/// Source: SmtpInternalService.cs lines 500-513
		/// </summary>
		public void SaveEmail(Email email)
		{
			PrepareEmailXSearch(email);
			RecordManager recMan = new RecordManager();
			var response = recMan.Find(new EntityQuery("email", "*", EntityQuery.QueryEQ("id", email.Id)));
			if (response.Object != null && response.Object.Data != null && response.Object.Data.Count != 0)
				response = recMan.UpdateRecord("email", email.MapTo<EntityRecord>());
			else
				response = recMan.CreateRecord("email", email.MapTo<EntityRecord>());

			if (!response.Success)
				throw new Exception(response.Message);
		}

		/// <summary>
		/// Retrieves an email by its unique identifier.
		/// Source: SmtpInternalService.cs lines 674-681
		/// </summary>
		public Email GetEmail(Guid id)
		{
			var result = new EqlCommand("SELECT * FROM email WHERE id = @id", new EqlParameter("id", id)).Execute();
			if (result.Count == 1)
				return result[0].MapTo<Email>();

			return null;
		}

		/// <summary>
		/// Prepares the x_search full-text search field for an email record.
		/// Concatenates sender, recipients, subject, and content fields.
		/// Source: SmtpInternalService.cs lines 683-687
		/// </summary>
		public void PrepareEmailXSearch(Email email)
		{
			var recipientsText = string.Join(" ", email.Recipients.Select(x => $"{x.Name} {x.Address}"));
			email.XSearch = $"{email.Sender?.Name} {email.Sender?.Address} {recipientsText} {email.Subject} {email.ContentText} {email.ContentHtml}";
		}

		#endregion

		#region <--- Content Manipulation (from SmtpInternalService.cs) --->

		/// <summary>
		/// Processes HTML content in a BodyBuilder — finds inline images with /fs paths,
		/// embeds them as CID-linked resources, and generates plaintext if missing.
		/// Source: SmtpInternalService.cs lines 518-582
		/// </summary>
		public static void ProcessHtmlContent(BodyBuilder builder)
		{
			if (builder == null)
				return;

			if (string.IsNullOrWhiteSpace(builder.HtmlBody))
				return;

			try
			{
				var htmlDoc = new HtmlDocument();
				htmlDoc.Load(new MemoryStream(Encoding.UTF8.GetBytes(builder.HtmlBody)));

				if (htmlDoc.DocumentNode == null)
					return;

				foreach (HtmlNode node in htmlDoc.DocumentNode.SelectNodes("//img[@src]"))
				{
					var src = node.Attributes["src"].Value.Split('?', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();


					if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("/fs"))
					{
						try
						{
							Uri uri = new Uri(src);
							src = uri.AbsolutePath;
						}
						catch { }

						if (src.StartsWith("/fs"))
							src = src.Substring(3);

						DbFileRepository fsRepository = new DbFileRepository();
						var file = fsRepository.Find(src);
						if (file == null)
							continue;

						var bytes = file.GetBytes();

						var extension = Path.GetExtension(src).ToLowerInvariant();
						new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

						var imagePart = new MimePart(mimeType)
						{
							ContentId = MimeUtils.GenerateMessageId(),
							Content = new MimeContent(new MemoryStream(bytes)),
							ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
							ContentTransferEncoding = ContentEncoding.Base64,
							FileName = Path.GetFileName(src)
						};

						builder.LinkedResources.Add(imagePart);
						node.SetAttributeValue("src", $"cid:{imagePart.ContentId}");
					}
				}
				builder.HtmlBody = htmlDoc.DocumentNode.OuterHtml;
				if (string.IsNullOrWhiteSpace(builder.TextBody) && !string.IsNullOrWhiteSpace(builder.HtmlBody))
					builder.TextBody = ConvertToPlainText(builder.HtmlBody);
			}
			catch
			{
				return;
			}
		}

		/// <summary>
		/// Converts HTML content to plain text.
		/// Source: SmtpInternalService.cs lines 585-604
		/// </summary>
		private static string ConvertToPlainText(string html)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(html))
					return string.Empty;

				HtmlDocument doc = new HtmlDocument();
				doc.LoadHtml(html);

				StringWriter sw = new StringWriter();
				ConvertTo(doc.DocumentNode, sw);
				sw.Flush();
				return sw.ToString();
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>
		/// Iterates child nodes and calls ConvertTo recursively.
		/// Source: SmtpInternalService.cs lines 606-612
		/// </summary>
		private static void ConvertContentTo(HtmlNode node, TextWriter outText)
		{
			foreach (HtmlNode subnode in node.ChildNodes)
			{
				ConvertTo(subnode, outText);
			}
		}

		/// <summary>
		/// Converts a single HTML node to plain text output.
		/// Handles: Comment (skip), Document (recurse), Text (skip script/style, decode entities),
		/// Element (p→newline, br→newline, a→output href).
		/// Source: SmtpInternalService.cs lines 614-669
		/// </summary>
		private static void ConvertTo(HtmlNode node, TextWriter outText)
		{
			string html;
			switch (node.NodeType)
			{
				case HtmlNodeType.Comment:
					// don't output comments
					break;

				case HtmlNodeType.Document:
					ConvertContentTo(node, outText);
					break;

				case HtmlNodeType.Text:
					// script and style must not be output
					string parentName = node.ParentNode.Name;
					if ((parentName == "script") || (parentName == "style"))
						break;

					// get text
					html = ((HtmlTextNode)node).Text;

					// is it in fact a special closing node output as text?
					if (HtmlNode.IsOverlappedClosingElement(html))
						break;

					// check the text is meaningful and not a bunch of white spaces
					if (html.Trim().Length > 0)
					{
						outText.Write(HtmlEntity.DeEntitize(html));
					}
					break;

				case HtmlNodeType.Element:
					switch (node.Name)
					{
						case "p":
							// treat paragraphs as crlf
							outText.Write(Environment.NewLine);
							break;
						case "br":
							outText.Write(Environment.NewLine);
							break;
						case "a":
							HtmlAttribute att = node.Attributes["href"];
							outText.Write($"<{att.Value}>");
							break;
					}

					if (node.HasChildNodes)
					{
						ConvertContentTo(node, outText);
					}
					break;
			}
		}

		#endregion

		#region <--- SendEmail Overloads (from Api/SmtpService.cs) --->

		/// <summary>
		/// Sends an email to a single recipient using default sender.
		/// Source: Api/SmtpService.cs lines 67-195
		/// </summary>
		public void SendEmail(SmtpServiceConfig config, EmailAddress recipient, string subject, string textBody, string htmlBody, List<string> attachments)
		{
			ValidationException ex = new ValidationException();

			if (recipient == null)
				ex.AddError("recipientEmail", "Recipient is not specified.");
			else
			{
				if (string.IsNullOrEmpty(recipient.Address))
					ex.AddError("recipientEmail", "Recipient email is not specified.");
				else if (!recipient.Address.IsEmail())
					ex.AddError("recipientEmail", "Recipient email is not valid email address.");
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			var message = new MimeMessage();
			if (!string.IsNullOrWhiteSpace(config.DefaultSenderName))
				message.From.Add(new MailboxAddress(config.DefaultSenderName, config.DefaultSenderEmail));
			else
				message.From.Add(new MailboxAddress(config.DefaultSenderEmail, config.DefaultSenderEmail));

			if (!string.IsNullOrWhiteSpace(recipient.Name))
				message.To.Add(new MailboxAddress(recipient.Name, recipient.Address));
			else
				message.To.Add(new MailboxAddress(recipient.Address, recipient.Address));

			if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail))
				message.ReplyTo.Add(new MailboxAddress(config.DefaultReplyToEmail, config.DefaultReplyToEmail));

			message.Subject = subject;

			var bodyBuilder = new BodyBuilder();
			bodyBuilder.HtmlBody = htmlBody;
			bodyBuilder.TextBody = textBody;

			if (attachments != null && attachments.Count > 0)
			{
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					DbFileRepository fsRepository = new DbFileRepository();
					var file = fsRepository.Find(filepath);
					var bytes = file.GetBytes();

					var extension = Path.GetExtension(filepath).ToLowerInvariant();
					new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

					var attachment = new MimePart(mimeType)
					{
						Content = new MimeContent(new MemoryStream(bytes)),
						ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
						ContentTransferEncoding = ContentEncoding.Base64,
						FileName = Path.GetFileName(filepath)
					};

					bodyBuilder.Attachments.Add(attachment);
				}
			}

			ProcessHtmlContent(bodyBuilder);
			message.Body = bodyBuilder.ToMessageBody();

			using (var client = new SmtpClient())
			{
				//accept all SSL certificates (in case the server supports STARTTLS)
				client.ServerCertificateValidationCallback = (s, c, h, e) => true;

				client.Connect(config.Server, config.Port, config.ConnectionSecurity);

				if (!string.IsNullOrWhiteSpace(config.Username))
					client.Authenticate(config.Username, config.Password);

				client.Send(message);
				client.Disconnect(true);
			}

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = new List<EmailAddress> { recipient };
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = email.CreatedOn;
			email.Priority = EmailPriority.Normal;
			email.Status = EmailStatus.Sent;
			email.ServerError = string.Empty;
			email.ScheduledOn = null;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}
			SaveEmail(email);
		}

		/// <summary>
		/// Sends an email to multiple recipients using default sender.
		/// Source: Api/SmtpService.cs lines 197-338
		/// </summary>
		public void SendEmail(SmtpServiceConfig config, List<EmailAddress> recipients, string subject, string textBody, string htmlBody, List<string> attachments)
		{
			ValidationException ex = new ValidationException();

			if (recipients == null || recipients.Count == 0)
			{
				ex.AddError("recipientEmail", "Recipient is not specified.");
			}
			else
			{
				foreach (var recipient in recipients)
				{
					if (recipient == null)
						ex.AddError("recipientEmail", "Recipient is not specified.");
					else
					{
						if (string.IsNullOrEmpty(recipient.Address))
							ex.AddError("recipientEmail", "Recipient email is not specified.");
						else if (!recipient.Address.IsEmail())
							ex.AddError("recipientEmail", "Recipient email is not valid email address.");
					}
				}
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			var message = new MimeMessage();
			if (!string.IsNullOrWhiteSpace(config.DefaultSenderName))
				message.From.Add(new MailboxAddress(config.DefaultSenderName, config.DefaultSenderEmail));
			else
				message.From.Add(new MailboxAddress(config.DefaultSenderEmail, config.DefaultSenderEmail));

			foreach (var recipient in recipients)
			{
				if (!string.IsNullOrWhiteSpace(recipient.Name))
					message.To.Add(new MailboxAddress(recipient.Name, recipient.Address));
				else
					message.To.Add(new MailboxAddress(recipient.Address, recipient.Address));
			}

			if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail))
				message.ReplyTo.Add(new MailboxAddress(config.DefaultReplyToEmail, config.DefaultReplyToEmail));

			message.Subject = subject;

			var bodyBuilder = new BodyBuilder();
			bodyBuilder.HtmlBody = htmlBody;
			bodyBuilder.TextBody = textBody;

			if (attachments != null && attachments.Count > 0)
			{
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					DbFileRepository fsRepository = new DbFileRepository();
					var file = fsRepository.Find(filepath);
					var bytes = file.GetBytes();

					var extension = Path.GetExtension(filepath).ToLowerInvariant();
					new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

					var attachment = new MimePart(mimeType)
					{
						Content = new MimeContent(new MemoryStream(bytes)),
						ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
						ContentTransferEncoding = ContentEncoding.Base64,
						FileName = Path.GetFileName(filepath)
					};

					bodyBuilder.Attachments.Add(attachment);
				}
			}

			ProcessHtmlContent(bodyBuilder);
			message.Body = bodyBuilder.ToMessageBody();

			using (var client = new SmtpClient())
			{
				//accept all SSL certificates (in case the server supports STARTTLS)
				client.ServerCertificateValidationCallback = (s, c, h, e) => true;

				client.Connect(config.Server, config.Port, config.ConnectionSecurity);

				if (!string.IsNullOrWhiteSpace(config.Username))
					client.Authenticate(config.Username, config.Password);

				client.Send(message);
				client.Disconnect(true);
			}

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = recipients;
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = email.CreatedOn;
			email.Priority = EmailPriority.Normal;
			email.Status = EmailStatus.Sent;
			email.ServerError = string.Empty;
			email.ScheduledOn = null;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}
			SaveEmail(email);
		}

		/// <summary>
		/// Sends an email to a single recipient with explicit sender.
		/// Source: Api/SmtpService.cs lines 340-467
		/// </summary>
		public void SendEmail(SmtpServiceConfig config, EmailAddress recipient, EmailAddress sender, string subject, string textBody, string htmlBody, List<string> attachments)
		{
			ValidationException ex = new ValidationException();

			if (recipient == null)
				ex.AddError("recipientEmail", "Recipient is not specified.");
			else
			{
				if (string.IsNullOrEmpty(recipient.Address))
					ex.AddError("recipientEmail", "Recipient email is not specified.");
				else if (!recipient.Address.IsEmail())
					ex.AddError("recipientEmail", "Recipient email is not valid email address.");
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			var message = new MimeMessage();
			if (!string.IsNullOrWhiteSpace(sender.Name))
				message.From.Add(new MailboxAddress(sender.Name, sender.Address));
			else
				message.From.Add(new MailboxAddress(sender.Address, sender.Address));

			if (!string.IsNullOrWhiteSpace(recipient.Name))
				message.To.Add(new MailboxAddress(recipient.Name, recipient.Address));
			else
				message.To.Add(new MailboxAddress(recipient.Address, recipient.Address));

			if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail))
				message.ReplyTo.Add(new MailboxAddress(config.DefaultReplyToEmail, config.DefaultReplyToEmail));

			message.Subject = subject;

			var bodyBuilder = new BodyBuilder();
			bodyBuilder.HtmlBody = htmlBody;
			bodyBuilder.TextBody = textBody;

			if (attachments != null && attachments.Count > 0)
			{
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					DbFileRepository fsRepository = new DbFileRepository();
					var file = fsRepository.Find(filepath);
					var bytes = file.GetBytes();

					var extension = Path.GetExtension(filepath).ToLowerInvariant();
					new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

					var attachment = new MimePart(mimeType)
					{
						Content = new MimeContent(new MemoryStream(bytes)),
						ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
						ContentTransferEncoding = ContentEncoding.Base64,
						FileName = Path.GetFileName(filepath)
					};

					bodyBuilder.Attachments.Add(attachment);
				}
			}
			ProcessHtmlContent(bodyBuilder);
			message.Body = bodyBuilder.ToMessageBody();

			using (var client = new SmtpClient())
			{
				//accept all SSL certificates (in case the server supports STARTTLS)
				client.ServerCertificateValidationCallback = (s, c, h, e) => true;

				client.Connect(config.Server, config.Port, config.ConnectionSecurity);

				if (!string.IsNullOrWhiteSpace(config.Username))
					client.Authenticate(config.Username, config.Password);

				client.Send(message);
				client.Disconnect(true);
			}

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = sender;
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = new List<EmailAddress> { recipient };
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = email.CreatedOn;
			email.Priority = EmailPriority.Normal;
			email.Status = EmailStatus.Sent;
			email.ServerError = string.Empty;
			email.ScheduledOn = null;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}
			SaveEmail(email);
		}

		/// <summary>
		/// Sends an email to multiple recipients with explicit sender.
		/// Source: Api/SmtpService.cs lines 469-613
		/// </summary>
		public void SendEmail(SmtpServiceConfig config, List<EmailAddress> recipients, EmailAddress sender, string subject, string textBody, string htmlBody, List<string> attachments)
		{
			ValidationException ex = new ValidationException();

			if (recipients == null || recipients.Count == 0)
			{
				ex.AddError("recipientEmail", "Recipient is not specified.");
			}
			else
			{
				foreach (var recipient in recipients)
				{
					if (recipient == null)
						ex.AddError("recipientEmail", "Recipient is not specified.");
					else
					{
						if (string.IsNullOrEmpty(recipient.Address))
							ex.AddError("recipientEmail", "Recipient email is not specified.");
						else if (!recipient.Address.IsEmail())
							ex.AddError("recipientEmail", "Recipient email is not valid email address.");
					}
				}
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			var message = new MimeMessage();
			if (!string.IsNullOrWhiteSpace(sender.Name))
				message.From.Add(new MailboxAddress(sender.Name, sender.Address));
			else
				message.From.Add(new MailboxAddress(sender.Address, sender.Address));

			foreach (var recipient in recipients)
			{
				if (!string.IsNullOrWhiteSpace(recipient.Name))
					message.To.Add(new MailboxAddress(recipient.Name, recipient.Address));
				else
					message.To.Add(new MailboxAddress(recipient.Address, recipient.Address));
			}

			if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail))
				message.ReplyTo.Add(new MailboxAddress(config.DefaultReplyToEmail, config.DefaultReplyToEmail));

			message.Subject = subject;

			var bodyBuilder = new BodyBuilder();
			bodyBuilder.HtmlBody = htmlBody;
			bodyBuilder.TextBody = textBody;

			if (attachments != null && attachments.Count > 0)
			{
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					DbFileRepository fsRepository = new DbFileRepository();
					var file = fsRepository.Find(filepath);
					var bytes = file.GetBytes();

					var extension = Path.GetExtension(filepath).ToLowerInvariant();
					new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

					var attachment = new MimePart(mimeType)
					{
						Content = new MimeContent(new MemoryStream(bytes)),
						ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
						ContentTransferEncoding = ContentEncoding.Base64,
						FileName = Path.GetFileName(filepath)
					};

					bodyBuilder.Attachments.Add(attachment);
				}
			}
			ProcessHtmlContent(bodyBuilder);
			message.Body = bodyBuilder.ToMessageBody();

			using (var client = new SmtpClient())
			{
				//accept all SSL certificates (in case the server supports STARTTLS)
				client.ServerCertificateValidationCallback = (s, c, h, e) => true;

				client.Connect(config.Server, config.Port, config.ConnectionSecurity);

				if (!string.IsNullOrWhiteSpace(config.Username))
					client.Authenticate(config.Username, config.Password);

				client.Send(message);
				client.Disconnect(true);
			}

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = sender;
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = recipients;
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = email.CreatedOn;
			email.Priority = EmailPriority.Normal;
			email.Status = EmailStatus.Sent;
			email.ServerError = string.Empty;
			email.ScheduledOn = null;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;
			email.Attachments = new List<string>();
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}

			SaveEmail(email);
		}

		#endregion

		#region <--- Internal SendEmail with Retry (from SmtpInternalService.cs) --->

		/// <summary>
		/// Internal send email with retry logic — used by ProcessSmtpQueue.
		/// Handles cc:/bcc: prefixes, semicolon-delimited ReplyTo, retry scheduling.
		/// Source: SmtpInternalService.cs lines 689-827
		/// </summary>
		public void SendEmail(Email email, SmtpServiceConfig service)
		{
			try
			{

				if (service == null)
				{
					email.ServerError = "SMTP service not found";
					email.Status = EmailStatus.Aborted;
					return; //save email in finally block will save changes
				}
				else if (!service.IsEnabled)
				{
					email.ServerError = "SMTP service is not enabled";
					email.Status = EmailStatus.Aborted;
					return; //save email in finally block will save changes
				}

				var message = new MimeMessage();
				if (!string.IsNullOrWhiteSpace(email.Sender?.Name))
					message.From.Add(new MailboxAddress(email.Sender?.Name, email.Sender?.Address));
				else
					message.From.Add(new MailboxAddress(email.Sender?.Address, email.Sender?.Address));

				foreach (var recipient in email.Recipients)
				{
					if (recipient.Address.StartsWith("cc:"))
					{
						if (!string.IsNullOrWhiteSpace(recipient.Name))
							message.Cc.Add(new MailboxAddress(recipient.Name, recipient.Address.Substring(3)));
						else
							message.Cc.Add(new MailboxAddress(recipient.Address.Substring(3), recipient.Address.Substring(3)));
					}
					else if (recipient.Address.StartsWith("bcc:"))
					{
						if (!string.IsNullOrWhiteSpace(recipient.Name))
							message.Bcc.Add(new MailboxAddress(recipient.Name, recipient.Address.Substring(4)));
						else
							message.Bcc.Add(new MailboxAddress(recipient.Address.Substring(4), recipient.Address.Substring(4)));
					}
					else
					{
						if (!string.IsNullOrWhiteSpace(recipient.Name))
							message.To.Add(new MailboxAddress(recipient.Name, recipient.Address));
						else
							message.To.Add(new MailboxAddress(recipient.Address, recipient.Address));
					}
				}

				if (!string.IsNullOrWhiteSpace(email.ReplyToEmail))
				{
					string[] replyToEmails = email.ReplyToEmail.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
					foreach (var replyEmail in replyToEmails)
						message.ReplyTo.Add(new MailboxAddress(replyEmail, replyEmail));
				}
				else
					message.ReplyTo.Add(new MailboxAddress(email.Sender?.Address, email.Sender?.Address));

				message.Subject = email.Subject;

				var bodyBuilder = new BodyBuilder();
				bodyBuilder.HtmlBody = email.ContentHtml;
				bodyBuilder.TextBody = email.ContentText;

				if (email.Attachments != null && email.Attachments.Count > 0)
				{
					foreach (var att in email.Attachments)
					{
						var filepath = att;

						if (!filepath.StartsWith("/"))
							filepath = "/" + filepath;

						filepath = filepath.ToLowerInvariant();

						if (filepath.StartsWith("/fs"))
							filepath = filepath.Substring(3);

						DbFileRepository fsRepository = new DbFileRepository();
						var file = fsRepository.Find(filepath);
						var bytes = file.GetBytes();

						var extension = Path.GetExtension(filepath).ToLowerInvariant();
						new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

						var attachment = new MimePart(mimeType)
						{
							Content = new MimeContent(new MemoryStream(bytes)),
							ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
							ContentTransferEncoding = ContentEncoding.Base64,
							FileName = Path.GetFileName(filepath)
						};

						bodyBuilder.Attachments.Add(attachment);
					}
				}
				ProcessHtmlContent(bodyBuilder);
				message.Body = bodyBuilder.ToMessageBody();

				using (var client = new SmtpClient())
				{
					//accept all SSL certificates (in case the server supports STARTTLS)
					client.ServerCertificateValidationCallback = (s, c, h, e) => true;

					client.Connect(service.Server, service.Port, service.ConnectionSecurity);

					if (!string.IsNullOrWhiteSpace(service.Username))
						client.Authenticate(service.Username, service.Password);

					client.Send(message);
					client.Disconnect(true);
				}
				email.SentOn = DateTime.UtcNow;
				email.Status = EmailStatus.Sent;
				email.ScheduledOn = null;
				email.ServerError = null;
			}
			catch (Exception ex)
			{
				email.SentOn = null;
				email.ServerError = ex.Message;
				email.RetriesCount++;
				if (email.RetriesCount >= service.MaxRetriesCount)
				{
					email.ScheduledOn = null;
					email.Status = EmailStatus.Aborted;
				}
				else
				{
					email.ScheduledOn = DateTime.UtcNow.AddMinutes(service.RetryWaitMinutes);
					email.Status = EmailStatus.Pending;
				}

			}
			finally
			{
				SaveEmail(email);
			}
		}

		#endregion

		#region <--- QueueEmail Overloads (from Api/SmtpService.cs) --->

		/// <summary>
		/// Queues an email for a single recipient using default sender.
		/// Source: Api/SmtpService.cs lines 615-683
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, EmailAddress recipient, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			ValidationException ex = new ValidationException();

			if (recipient == null)
				ex.AddError("recipientEmail", "Recipient is not specified.");
			else
			{
				var address = recipient.Address;
				if (address.StartsWith("cc:"))
					address = address.Substring(3);

				if (address.StartsWith("bcc:"))
					address = address.Substring(4);

				if (string.IsNullOrEmpty(address))
					ex.AddError("recipientEmail", "Recipient email is not specified.");
				else if (!address.IsEmail())
					ex.AddError("recipientEmail", "Recipient email is not valid email address.");
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = new List<EmailAddress> { recipient };
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = null;
			email.Priority = priority;
			email.Status = EmailStatus.Pending;
			email.ServerError = string.Empty;
			email.ScheduledOn = email.CreatedOn;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;

			email.Attachments = new List<string>();
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}

			SaveEmail(email);
		}

		/// <summary>
		/// Queues an email for multiple recipients using default sender.
		/// Source: Api/SmtpService.cs lines 685-763
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, List<EmailAddress> recipients, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			ValidationException ex = new ValidationException();

			if (recipients == null || recipients.Count == 0)
			{
				ex.AddError("recipientEmail", "Recipient is not specified.");
			}
			else
			{
				foreach (var recipient in recipients)
				{
					if (recipient == null)
						ex.AddError("recipientEmail", "Recipient is not specified.");
					else
					{
						var address = recipient.Address;
						if (address.StartsWith("cc:"))
							address = address.Substring(3);

						if (address.StartsWith("bcc:"))
							address = address.Substring(4);

						if (string.IsNullOrEmpty(address))
							ex.AddError("recipientEmail", "Recipient email is not specified.");
						else if (!address.IsEmail())
							ex.AddError("recipientEmail", "Recipient email is not valid email address.");
					}
				}
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			email.ReplyToEmail = config.DefaultReplyToEmail;
			email.Recipients = recipients;
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = null;
			email.Priority = priority;
			email.Status = EmailStatus.Pending;
			email.ServerError = string.Empty;
			email.ScheduledOn = email.CreatedOn;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;

			email.Attachments = new List<string>();
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}

			SaveEmail(email);
		}

		/// <summary>
		/// Queues an email for a single recipient with explicit sender.
		/// Delegates to full overload with null replyTo.
		/// Source: Api/SmtpService.cs lines 765-768
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, EmailAddress recipient, EmailAddress sender, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			QueueEmail(config, recipient, sender, null, subject, textBody, htmlBody, priority, attachments);
		}

		/// <summary>
		/// Queues an email for multiple recipients with explicit sender.
		/// Delegates to full overload with null replyTo.
		/// Source: Api/SmtpService.cs lines 770-772
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, List<EmailAddress> recipients, EmailAddress sender, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			QueueEmail(config, recipients, sender, null, subject, textBody, htmlBody, priority, attachments);
		}

		/// <summary>
		/// Queues an email for a single recipient with explicit sender and replyTo.
		/// Full overload with all parameters. Validates replyTo (semicolon-split, each must be IsEmail).
		/// Source: Api/SmtpService.cs lines 775-854
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, EmailAddress recipient, EmailAddress sender, string replyTo, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			ValidationException ex = new ValidationException();

			if (recipient == null)
				ex.AddError("recipientEmail", "Recipient is not specified.");
			else
			{
				var address = recipient.Address;
				if (address.StartsWith("cc:"))
					address = address.Substring(3);
				if (address.StartsWith("bcc:"))
					address = address.Substring(4);
				if (string.IsNullOrEmpty(address))
					ex.AddError("recipientEmail", "Recipient email is not specified.");
				else if (!address.IsEmail())
					ex.AddError("recipientEmail", "Recipient email is not valid email address.");
			}

			if (!string.IsNullOrWhiteSpace(replyTo))
			{
				string[] replyToEmails = replyTo.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (var replyEmail in replyToEmails)
				{
					if (!replyEmail.IsEmail())
						ex.AddError("recipientEmail", "Reply To email is not valid email address.");
				}
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = sender ?? new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			if (string.IsNullOrWhiteSpace(replyTo))
				email.ReplyToEmail = config.DefaultReplyToEmail;
			else
				email.ReplyToEmail = replyTo;
			email.Recipients = new List<EmailAddress> { recipient };
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = null;
			email.Priority = priority;
			email.Status = EmailStatus.Pending;
			email.ServerError = string.Empty;
			email.ScheduledOn = email.CreatedOn;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;

			email.Attachments = new List<string>();
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}

			SaveEmail(email);
		}

		/// <summary>
		/// Queues an email for multiple recipients with explicit sender and replyTo.
		/// Full overload with all parameters.
		/// Source: Api/SmtpService.cs lines 856-945
		/// </summary>
		public void QueueEmail(SmtpServiceConfig config, List<EmailAddress> recipients, EmailAddress sender, string replyTo, string subject, string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal, List<string> attachments = null)
		{
			ValidationException ex = new ValidationException();

			if (recipients == null || recipients.Count == 0)
			{
				ex.AddError("recipientEmail", "Recipient is not specified.");
			}
			else
			{
				foreach (var recipient in recipients)
				{
					if (recipient == null)
						ex.AddError("recipientEmail", "Recipient is not specified.");
					else
					{
						var address = recipient.Address;
						if (address.StartsWith("cc:"))
							address = address.Substring(3);
						if (address.StartsWith("bcc:"))
							address = address.Substring(4);
						if (string.IsNullOrEmpty(address))
							ex.AddError("recipientEmail", "Recipient email is not specified.");
						else if (!address.IsEmail())
							ex.AddError("recipientEmail", "Recipient email is not valid email address.");
					}
				}
			}

			if (!string.IsNullOrWhiteSpace(replyTo))
			{
				string[] replyToEmails = replyTo.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (var replyEmail in replyToEmails)
				{
					if (!replyEmail.IsEmail())
						ex.AddError("recipientEmail", "Reply To email is not valid email address.");
				}
			}

			if (string.IsNullOrEmpty(subject))
				ex.AddError("subject", "Subject is required.");

			ex.CheckAndThrow();

			Email email = new Email();
			email.Id = Guid.NewGuid();
			email.Sender = sender ?? new EmailAddress { Address = config.DefaultSenderEmail, Name = config.DefaultSenderName };
			if (string.IsNullOrWhiteSpace(replyTo))
				email.ReplyToEmail = config.DefaultReplyToEmail;
			else
				email.ReplyToEmail = replyTo;
			email.Recipients = recipients;
			email.Subject = subject;
			email.ContentHtml = htmlBody;
			email.ContentText = textBody;
			email.CreatedOn = DateTime.UtcNow;
			email.SentOn = null;
			email.Priority = priority;
			email.Status = EmailStatus.Pending;
			email.ServerError = string.Empty;
			email.ScheduledOn = email.CreatedOn;
			email.RetriesCount = 0;
			email.ServiceId = config.Id;

			email.Attachments = new List<string>();
			if (attachments != null && attachments.Count > 0)
			{
				DbFileRepository fsRepository = new DbFileRepository();
				foreach (var att in attachments)
				{
					var filepath = att;

					if (!filepath.StartsWith("/"))
						filepath = "/" + filepath;

					filepath = filepath.ToLowerInvariant();

					if (filepath.StartsWith("/fs"))
						filepath = filepath.Substring(3);

					var file = fsRepository.Find(filepath);
					if (file == null)
						throw new Exception($"Attachment file '{filepath}' not found.");

					email.Attachments.Add(filepath);
				}
			}

			SaveEmail(email);
		}

		#endregion

		#region <--- Queue Processing (from SmtpInternalService.cs) --->

		/// <summary>
		/// Processes the SMTP email queue. Runs under a static lock to prevent concurrent processing.
		/// Fetches pending emails in batches of 10 ordered by priority DESC, scheduled_on ASC.
		/// Source: SmtpInternalService.cs lines 829-878
		/// </summary>
		public void ProcessSmtpQueue()
		{
			lock (lockObject)
			{
				if (queueProcessingInProgress)
					return;

				queueProcessingInProgress = true;
			}

			try
			{
				List<Email> pendingEmails = new List<Email>();
				do
				{
					pendingEmails = new EqlCommand("SELECT * FROM email WHERE status = @status AND scheduled_on <> NULL" +
													" AND scheduled_on < @scheduled_on  ORDER BY priority DESC, scheduled_on ASC PAGE 1 PAGESIZE 10",
								new EqlParameter("status", ((int)EmailStatus.Pending).ToString()),
								new EqlParameter("scheduled_on", DateTime.UtcNow)).Execute().MapTo<Email>();

					foreach (var email in pendingEmails)
					{
						var service = GetSmtpService(email.ServiceId);
						if (service == null)
						{
							email.Status = EmailStatus.Aborted;
							email.ServerError = "SMTP service not found.";
							email.ScheduledOn = null;
							SaveEmail(email);
							continue;
						}
						else
						{
							SendEmail(email, service);
						}
					}
				}
				while (pendingEmails.Count > 0);

			}
			finally
			{
				lock (lockObject)
				{
					queueProcessingInProgress = false;
				}
			}
		}

		#endregion
	}
}
