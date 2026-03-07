using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Utilities;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Controllers
{
	/// <summary>
	/// Core Platform File Management REST API Controller.
	/// Exposes file upload, download, move, delete, and user file management REST endpoints.
	/// Extracted from the monolith's <c>WebApiController.cs</c> — specifically the file system
	/// operations and user file CRUD endpoints.
	///
	/// Preserves ALL original route patterns, HTTP caching behavior, CKEditor response formats,
	/// file type classification logic, and image dimension extraction using System.Drawing.Common.
	///
	/// All endpoints use the BaseResponseModel/ResponseModel/FSResponse response envelopes
	/// to maintain backward compatibility with the REST API v3 contract.
	/// </summary>
	[Authorize]
	[ApiController]
	public class FileController : Controller
	{
		private readonly DbFileRepository _fileRepository;
		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly SecurityManager _securityManager;

		/// <summary>
		/// Constructs a FileController with all required service dependencies via DI.
		/// </summary>
		/// <param name="fileRepository">Primary file storage repository for all file lifecycle operations.</param>
		/// <param name="recordManager">Core record CRUD manager for creating and querying user_file entity records.</param>
		/// <param name="entityManager">Entity metadata CRUD manager for resolving entity definitions.</param>
		/// <param name="securityManager">User/role operations manager for resolving the current authenticated user.</param>
		public FileController(
			DbFileRepository fileRepository,
			RecordManager recordManager,
			EntityManager entityManager,
			SecurityManager securityManager)
		{
			_fileRepository = fileRepository ?? throw new ArgumentNullException(nameof(fileRepository));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
		}

		#region << Response Helper Methods >>

		/// <summary>
		/// Standard response helper that sets the HTTP status code based on the response model's
		/// error state and returns the response as JSON.
		/// Preserved from monolith ApiControllerBase.DoResponse (lines 16-30).
		/// </summary>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			if (response.Errors.Count > 0 || !response.Success)
			{
				if (response.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)response.StatusCode;
			}

			return Json(response);
		}

		/// <summary>
		/// Returns a 404 Not Found response with an empty JSON body.
		/// Preserved from monolith ApiControllerBase.DoPageNotFoundResponse (lines 32-36).
		/// </summary>
		protected IActionResult DoPageNotFoundResponse()
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(new { });
		}

		/// <summary>
		/// Returns a 404 Not Found response wrapping the provided response model.
		/// Preserved from monolith ApiControllerBase.DoItemNotFoundResponse (lines 38-42).
		/// </summary>
		protected IActionResult DoItemNotFoundResponse(BaseResponseModel response)
		{
			HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
			return Json(response);
		}

		/// <summary>
		/// Returns a 400 Bad Request response. Sets Success=false, populates error message
		/// based on development mode, and returns JSON.
		/// Preserved from monolith ApiControllerBase.DoBadRequestResponse (lines 44-62).
		/// </summary>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			if (ErpSettings.DevelopmentMode)
			{
				if (ex != null)
					response.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
				else
					response.Message = message;
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(response);
		}

		#endregion

		#region << Files >>

		/// <summary>
		/// Downloads a file from the file storage system.
		/// Supports nested folder paths via multiple route parameters (workaround for
		/// conflict with Razor Pages routing and wildcard controller routing).
		///
		/// Implements HTTP caching via If-Modified-Since / 304 Not Modified responses
		/// and sets Cache-Control / Last-Modified response headers for 30-day browser caching.
		///
		/// Preserved from monolith WebApiController.Download (lines 3252-3324).
		/// </summary>
		/// <param name="fileName">The file name to download.</param>
		/// <param name="root">Optional first folder segment.</param>
		/// <param name="root2">Optional second folder segment.</param>
		/// <param name="root3">Optional third folder segment.</param>
		/// <param name="root4">Optional fourth folder segment.</param>
		/// <returns>File content with appropriate MIME type, or 404/304 status.</returns>
		[AllowAnonymous]
		[HttpGet("/fs/{fileName}")]
		[HttpGet("/fs/{root}/{fileName}")]
		[HttpGet("/fs/{root}/{root2}/{fileName}")]
		[HttpGet("/fs/{root}/{root2}/{root3}/{fileName}")]
		[HttpGet("/fs/{root}/{root2}/{root3}/{root4}/{fileName}")]
		public IActionResult DownloadFile(
			[FromRoute] string fileName,
			[FromRoute] string root = null,
			[FromRoute] string root2 = null,
			[FromRoute] string root3 = null,
			[FromRoute] string root4 = null)
		{
			// We added ROOT routing parameter as workaround for conflict with razor pages routing
			// and wildcard controller routing. In particular we have problem with ApplicationNodePage
			// where routing pattern is "/{AppName}/{AreaName}/{NodeName}/a/{PageName?}"

			if (string.IsNullOrWhiteSpace(fileName))
				return DoPageNotFoundResponse();

			// Path traversal protection (CWE-22): reject path segments containing
			// directory traversal characters to prevent unauthorized file access.
			var segments = new[] { root, root2, root3, root4, fileName };
			foreach (var seg in segments)
			{
				if (seg != null && (seg.Contains("..") || seg.Contains("~") || seg.Contains('\0')))
					return DoPageNotFoundResponse();
			}

			var filePathArray = new List<string>();
			if (root != null) filePathArray.Add(root);
			if (root2 != null) filePathArray.Add(root2);
			if (root3 != null) filePathArray.Add(root3);
			if (root4 != null) filePathArray.Add(root4);

			var filePath = "/" + String.Join("/", filePathArray) + "/" + fileName;
			filePath = filePath.ToLowerInvariant();

			var file = _fileRepository.Find(filePath);

			if (file == null)
			{
				return DoPageNotFoundResponse();
			}

			// Check for modification — HTTP caching with If-Modified-Since header
			string headerModifiedSince = Request.Headers["If-Modified-Since"];
			if (headerModifiedSince != null)
			{
				if (DateTime.TryParse(headerModifiedSince, out DateTime isModifiedSince))
				{
					if (isModifiedSince <= file.LastModificationDate)
					{
						Response.StatusCode = 304;
						return new EmptyResult();
					}
				}
			}

			var cultureInfo = new CultureInfo("en-US");
			HttpContext.Response.Headers["last-modified"] = file.LastModificationDate.ToString(cultureInfo);
			const int durationInSeconds = 60 * 60 * 24 * 30; // 30 days caching of these resources
			HttpContext.Response.Headers[HeaderNames.CacheControl] = "public,max-age=" + durationInSeconds;

			var extension = Path.GetExtension(filePath).ToLowerInvariant();
			new FileExtensionContentTypeProvider().Mappings.TryGetValue(extension, out string mimeType);

			return File(file.GetBytes(), mimeType ?? "application/octet-stream");
		}

		/// <summary>
		/// Uploads a single file to a temporary storage location.
		/// Returns an FSResponse with the created file's URL and filename.
		///
		/// Preserved from monolith WebApiController.UploadFile (lines 3327-3345).
		/// </summary>
		/// <param name="file">The uploaded file from the multipart form data.</param>
		/// <returns>FSResponse with file URL and filename.</returns>
		[HttpPost("/fs/upload/")]
		[ResponseCache(NoStore = true, Duration = 0)]
		[RequestSizeLimit(104_857_600)] // 100 MB upload limit to prevent DoS (CWE-400)
		public IActionResult UploadFile([FromForm] IFormFile file)
		{
			var fileName = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.ToString().Trim().ToLowerInvariant();
			if (fileName.StartsWith("\"", StringComparison.InvariantCulture))
				fileName = fileName.Substring(1);

			if (fileName.EndsWith("\"", StringComparison.InvariantCulture))
				fileName = fileName.Substring(0, fileName.Length - 1);

			var createdFile = _fileRepository.CreateTempFile(fileName, ReadFully(file.OpenReadStream()));

			return DoResponse(new FSResponse(new FSResult { Url = createdFile.FilePath, Filename = fileName }));
		}

		/// <summary>
		/// Moves a file from source path to target path.
		/// Accepts a JSON body with "source", "target", and optional "overwrite" properties.
		///
		/// Preserved from monolith WebApiController.MoveFile (lines 3347-3368).
		/// </summary>
		/// <param name="submitObj">JSON object containing source and target file paths.</param>
		/// <returns>FSResponse with the moved file's URL and filename.</returns>
		[HttpPost("/fs/move/")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult MoveFile([FromBody] JObject submitObj)
		{
			try
			{
				string source = submitObj["source"].Value<string>();
				string target = submitObj["target"].Value<string>();
				bool overwrite = false;
				if (submitObj["overwrite"] != null)
					overwrite = submitObj["overwrite"].Value<bool>();

				source = source.ToLowerInvariant();
				target = target.ToLowerInvariant();

				var fileName = target.Split(new char[] { '/' }).LastOrDefault();

				var sourceFile = _fileRepository.Find(source);
				if (sourceFile == null)
				{
					var errorResponse = new FSResponse();
					errorResponse.Success = false;
					errorResponse.Message = "Source file cannot be found.";
					return DoResponse(errorResponse);
				}

				var movedFile = _fileRepository.Move(source, target, overwrite);
				return DoResponse(new FSResponse(new FSResult { Url = movedFile.FilePath, Filename = fileName }));
			}
			catch (Exception ex)
			{
				var errorResponse = new FSResponse();
				errorResponse.Success = false;
				errorResponse.Message = ex.Message;
				return DoResponse(errorResponse);
			}
		}

		/// <summary>
		/// Deletes a file at the specified path.
		/// Uses catch-all route parameter for nested paths.
		///
		/// Preserved from monolith WebApiController.DeleteFile (lines 3370-3383).
		/// </summary>
		/// <param name="filepath">The full file path to delete (catch-all route parameter).</param>
		/// <returns>FSResponse with the deleted file's URL and filename.</returns>
		[HttpDelete("{*filepath}")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult DeleteFile([FromRoute] string filepath)
		{
			filepath = filepath.ToLowerInvariant();

			var fileName = filepath.Split(new char[] { '/' }).LastOrDefault();

			var sourceFile = _fileRepository.Find(filepath);
			_fileRepository.Delete(filepath);
			return DoResponse(new FSResponse(new FSResult { Url = filepath, Filename = fileName }));
		}

		#endregion

		#region << UserFile >>

		/// <summary>
		/// Returns a paginated, filtered list of user_file records.
		/// Supports filtering by file type (image, video, audio, document, other),
		/// search across name/alt/caption fields, and sort by created_on or name.
		///
		/// Preserved from monolith WebApiController.GetUserFileList (lines 3886-3904).
		/// UserFileService.GetFilesList logic is inlined here since the service layer
		/// is eliminated in the microservice architecture.
		/// </summary>
		/// <param name="type">Optional file type filter (image, video, audio, document, other).</param>
		/// <param name="search">Optional search term for name/alt/caption fields.</param>
		/// <param name="sort">Sort mode: 1 = created_on descending, 2 = name ascending. Default: 1.</param>
		/// <param name="page">Page number (1-based). Default: 1.</param>
		/// <param name="pageSize">Number of records per page. Default: 30.</param>
		/// <returns>ResponseModel containing the list of user_file records.</returns>
		[HttpGet("api/v3/en_US/user_file")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult GetUserFileList(
			[FromQuery] string type = "",
			[FromQuery] string search = "",
			[FromQuery] int sort = 1,
			[FromQuery] int page = 1,
			[FromQuery] int pageSize = 30)
		{
			var response = new ResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };

			try
			{
				var skipCount = (page - 1) * pageSize;

				var listSorts = new List<QuerySortObject>();
				switch (sort)
				{
					case 1:
						listSorts.Add(new QuerySortObject("created_on", QuerySortType.Descending));
						break;
					case 2:
						listSorts.Add(new QuerySortObject("name", QuerySortType.Ascending));
						break;
				}

				var filters = new List<QueryObject>();
				if (!String.IsNullOrWhiteSpace(search))
				{
					filters.Add(EntityQuery.QueryOR(
						EntityQuery.QueryContains("name", search),
						EntityQuery.QueryContains("alt", search),
						EntityQuery.QueryContains("caption", search)));
				}
				if (!String.IsNullOrWhiteSpace(type))
				{
					filters.Add(EntityQuery.QueryContains("type", type));
				}
				var filterQuery = EntityQuery.QueryAND(filters.ToArray());

				var query = new EntityQuery("user_file", "*", filterQuery, listSorts.ToArray(), skipCount, pageSize);
				var queryResponse = _recordManager.Find(query);
				if (!queryResponse.Success)
					throw new Exception(queryResponse.Message);

				response.Object = queryResponse.Object.Data;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Message = e.Message;
				if (ErpSettings.DevelopmentMode)
					response.Message = e.Message + e.StackTrace;
			}

			return DoResponse(response);
		}

		/// <summary>
		/// Creates a user_file record by accepting file metadata (path, alt, caption).
		/// Moves the file from its temporary location to a permanent path under /file/{newFileId}/,
		/// creates a user_file entity record with metadata (type, size, dimensions for images),
		/// and returns the created record.
		///
		/// Preserved from monolith WebApiController.UploadUserFile (lines 3906-3959)
		/// and UserFileService.CreateUserFile logic inlined.
		/// </summary>
		/// <param name="submitObj">JSON object with "path", optional "alt", and optional "caption" properties.</param>
		/// <returns>ResponseModel containing the created user_file record.</returns>
		[HttpPost("api/v3/en_US/user_file")]
		[ResponseCache(NoStore = true, Duration = 0)]
		public IActionResult UploadUserFile([FromBody] JObject submitObj)
		{
			var response = new ResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };
			var filePath = "";
			var fileAlt = "";
			var fileCaption = "";

			#region << Init SubmitObj >>
			foreach (var prop in submitObj.Properties())
			{
				switch (prop.Name.ToLower())
				{
					case "path":
						if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
							filePath = prop.Value.ToString();
						else
						{
							throw new Exception("File path is required");
						}
						break;
					case "alt":
						if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
							fileAlt = prop.Value.ToString();
						else
						{
							fileAlt = null;
						}
						break;
					case "caption":
						if (!string.IsNullOrWhiteSpace(prop.Value.ToString()))
							fileCaption = prop.Value.ToString();
						else
						{
							fileCaption = null;
						}
						break;
				}
			}
			#endregion

			try
			{
				// Inline UserFileService.CreateUserFile logic
				var userFileRecord = new EntityRecord();
				if (filePath.StartsWith("/fs"))
				{
					filePath = filePath.Substring(3);
				}
				var tempFile = _fileRepository.Find(filePath);
				if (tempFile == null)
				{
					throw new Exception("File not found on that path");
				}
				var newFileId = Guid.NewGuid();
				userFileRecord["id"] = newFileId;
				userFileRecord["alt"] = fileAlt;
				userFileRecord["caption"] = fileCaption;
				var fileKilobytes = Math.Round(((decimal)tempFile.GetBytes().Length / 1024), 2);
				userFileRecord["size"] = fileKilobytes;
				userFileRecord["name"] = Path.GetFileName(filePath);
				var fileExtension = Path.GetExtension(filePath);
				var mimeType = MimeMapping.MimeUtility.GetMimeMapping(filePath);
				if (mimeType.StartsWith("image"))
				{
					var dimensionsRecord = Helpers.GetImageDimension(tempFile.GetBytes());
					userFileRecord["width"] = (decimal)dimensionsRecord["width"];
					userFileRecord["height"] = (decimal)dimensionsRecord["height"];
					userFileRecord["type"] = "image";
				}
				else if (mimeType.StartsWith("video"))
				{
					userFileRecord["type"] = "video";
				}
				else if (mimeType.StartsWith("audio"))
				{
					userFileRecord["type"] = "audio";
				}
				else if (fileExtension == ".doc" || fileExtension == ".docx" || fileExtension == ".odt" || fileExtension == ".rtf"
				 || fileExtension == ".txt" || fileExtension == ".pdf" || fileExtension == ".html" || fileExtension == ".htm" || fileExtension == ".ppt"
				  || fileExtension == ".pptx" || fileExtension == ".xls" || fileExtension == ".xlsx" || fileExtension == ".ods" || fileExtension == ".odp")
				{
					userFileRecord["type"] = "document";
				}
				else
				{
					userFileRecord["type"] = "other";
				}

				var newFilePath = $"/file/{newFileId}/{Path.GetFileName(filePath)}";

				using (DbConnection con = CoreDbContext.Current.CreateConnection())
				{
					con.BeginTransaction();
					try
					{
						var file = _fileRepository.Move(filePath, newFilePath, false);
						if (file == null)
						{
							throw new Exception("File move from temp folder failed");
						}

						userFileRecord["path"] = newFilePath;
						var createResponse = _recordManager.CreateRecord("user_file", userFileRecord);
						if (!createResponse.Success)
							throw new Exception(createResponse.Message);

						userFileRecord = createResponse.Object.Data.First();
						con.CommitTransaction();
					}
					catch (Exception)
					{
						con.RollbackTransaction();
						throw;
					}
				}

				response.Object = userFileRecord;
			}
			catch (Exception e)
			{
				response.Success = false;
				response.Message = e.Message;
				if (ErpSettings.DevelopmentMode)
					response.Message = e.Message + e.StackTrace;
			}

			return DoResponse(response);
		}

		/// <summary>
		/// CKEditor drag-and-drop file upload endpoint.
		/// Uploads a file, creates a user_file record, and returns a CKEditor-compatible
		/// JSON response with "uploaded", "fileName", and "url" properties.
		///
		/// Preserved from monolith WebApiController.UploadDropCKEditor (lines 3962-4006).
		/// </summary>
		/// <param name="upload">The uploaded file from CKEditor drag-and-drop.</param>
		/// <returns>CKEditor-compatible JSON response.</returns>
		[HttpPost("/ckeditor/drop-upload-url")]
		[ResponseCache(NoStore = true, Duration = 0)]
		[RequestSizeLimit(104_857_600)] // 100 MB upload limit to prevent DoS (CWE-400)
		public IActionResult CKEditorDropUpload(IFormFile upload)
		{
			var response = new EntityRecord();
			byte[] fileBytes = null;
			try
			{
				if (upload != null)
				{
					using (var ms = new MemoryStream())
					{
						upload.CopyTo(ms);
						fileBytes = ms.ToArray();
					}
					var tempPath = "tmp/" + Guid.NewGuid() + "/" + upload.FileName;
					var tempFile = _fileRepository.Create(tempPath, fileBytes, null, null);

					// Inline UserFileService.CreateUserFile for the temp file
					var newFile = CreateUserFileInternal(tempFile.FilePath, null, null);

					string url = "/fs" + (string)newFile["path"];

					response["uploaded"] = 1;
					response["fileName"] = upload.FileName;
					response["url"] = url;
					return Json(response);
				}
				else
				{
					return Json(response);
				}
			}
			catch (Exception ex)
			{
				response["uploaded"] = 0;
				response["error"] = new EntityRecord();
				var message = new EntityRecord();
				message["message"] = ex.Message;
				response["error"] = message;
				return Json(response);
			}
		}

		/// <summary>
		/// CKEditor file manager image upload endpoint.
		/// Uploads a file, creates a user_file record, and returns an HTML response
		/// with a CKEDITOR.tools.callFunction callback for the CKEditor integration.
		///
		/// Preserved from monolith WebApiController.UploadFileManagerCKEditor (lines 4009-4039).
		/// </summary>
		/// <param name="upload">The uploaded image file from CKEditor.</param>
		/// <returns>HTML content with CKEditor callback script.</returns>
		[HttpPost("/ckeditor/image-upload-url")]
		[ResponseCache(NoStore = true, Duration = 0)]
		[RequestSizeLimit(104_857_600)] // 100 MB upload limit to prevent DoS (CWE-400)
		public IActionResult CKEditorUploadImage(IFormFile upload)
		{
			byte[] fileBytes = null;
			string CKEditorFuncNum = HttpContext.Request.Query["CKEditorFuncNum"].ToString();
			try
			{
				using (var ms = new MemoryStream())
				{
					upload.CopyTo(ms);
					fileBytes = ms.ToArray();
				}
				var tempPath = "tmp/" + Guid.NewGuid() + "/" + upload.FileName;
				var tempFile = _fileRepository.Create(tempPath, fileBytes, null, null);

				var newFile = CreateUserFileInternal(tempFile.FilePath, null, null);

				string url = "/fs" + (string)newFile["path"];
				string vMessage = "";
				var vOutput = @"<html><body><script>window.parent.CKEDITOR.tools.callFunction(" + CKEditorFuncNum + ", \"" + url + "\", \"" + vMessage + "\");</script></body></html>";

				return Content(vOutput, "text/html");
			}
			catch (Exception ex)
			{
				var vOutput = @"<html><body><script>window.parent.CKEDITOR.tools.callFunction(" + CKEditorFuncNum + ", \"\", \"" + ex.Message + "\");</script></body></html>";
				return Content(vOutput, "text/html");
			}
		}

		/// <summary>
		/// Uploads multiple files, creates user_file entity records for each,
		/// and returns the list of created records.
		///
		/// For each file:
		/// - Determines file type classification (image, video, audio, document, other)
		/// - For images: extracts dimensions using System.Drawing.Common
		/// - Saves file via DbFileRepository under /user_file/{userId}/{section}/{fileName}
		/// - Creates user_file entity record with metadata
		///
		/// Preserved from monolith WebApiController.UploadUserFileMultiple (lines 4041-4132).
		/// </summary>
		/// <param name="files">List of uploaded files from multipart form data.</param>
		/// <returns>ResponseModel containing list of created user_file records.</returns>
		[HttpPost("/fs/upload-user-file-multiple/")]
		[ResponseCache(NoStore = true, Duration = 0)]
		[RequestSizeLimit(104_857_600)] // 100 MB upload limit to prevent DoS (CWE-400)
		public IActionResult UploadUserFileMultiple([FromForm] List<IFormFile> files)
		{
			var resultRecords = new List<EntityRecord>();
			var response = new ResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();

				try
				{
					var currentUser = SecurityContext.CurrentUser;

					foreach (var file in files)
					{
						var fileBuffer = ReadFully(file.OpenReadStream());
						var fileName = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.ToString().Trim().ToLowerInvariant();
						if (fileName.StartsWith("\"", StringComparison.InvariantCulture))
							fileName = fileName.Substring(1);

						if (fileName.EndsWith("\"", StringComparison.InvariantCulture))
							fileName = fileName.Substring(0, fileName.Length - 1);

						string section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
						var filePath = "/user_file/" + currentUser.Id + "/" + section + "/" + fileName;
						var createdFile = _fileRepository.Create(filePath, fileBuffer, DateTime.Now, currentUser.Id);
						var userFileId = Guid.NewGuid();

						var userFileRecord = new EntityRecord();
						#region << record fill >>
						userFileRecord["id"] = userFileId;
						userFileRecord["created_on"] = DateTime.Now;
						userFileRecord["name"] = fileName;
						userFileRecord["size"] = Math.Round((decimal)(file.Length / 1024), 0);
						userFileRecord["path"] = filePath;

						var mimeType = MimeMapping.MimeUtility.GetMimeMapping(filePath);
						var fileExtension = Path.GetExtension(filePath);
						if (mimeType.StartsWith("image"))
						{
							var dimensionsRecord = Helpers.GetImageDimension(fileBuffer);
							userFileRecord["width"] = (decimal)dimensionsRecord["width"];
							userFileRecord["height"] = (decimal)dimensionsRecord["height"];
							userFileRecord["type"] = "image";
						}
						else if (mimeType.StartsWith("video"))
						{
							userFileRecord["type"] = "video";
						}
						else if (mimeType.StartsWith("audio"))
						{
							userFileRecord["type"] = "audio";
						}
						else if (fileExtension == ".doc" || fileExtension == ".docx" || fileExtension == ".odt" || fileExtension == ".rtf"
						 || fileExtension == ".txt" || fileExtension == ".pdf" || fileExtension == ".html" || fileExtension == ".htm" || fileExtension == ".ppt"
						  || fileExtension == ".pptx" || fileExtension == ".xls" || fileExtension == ".xlsx" || fileExtension == ".ods" || fileExtension == ".odp")
						{
							userFileRecord["type"] = "document";
						}
						else
						{
							userFileRecord["type"] = "other";
						}
						#endregion

						var recordCreateResult = _recordManager.CreateRecord("user_file", userFileRecord);
						if (!recordCreateResult.Success)
						{
							throw new Exception(recordCreateResult.Message);
						}
						resultRecords.Add(userFileRecord);
					}
					connection.CommitTransaction();
					response.Success = true;
					response.Object = resultRecords;
					return DoResponse(response);
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}
		}

		/// <summary>
		/// Uploads multiple files to temporary storage without creating user_file records.
		/// Returns metadata for each uploaded file including type classification and
		/// image dimensions.
		///
		/// Simpler than UploadUserFileMultiple — just stores files as temp and returns paths.
		///
		/// Preserved from monolith WebApiController.UploadFileMultiple (lines 4134-4214).
		/// </summary>
		/// <param name="files">List of uploaded files from multipart form data.</param>
		/// <returns>ResponseModel containing list of file metadata records.</returns>
		[HttpPost("/fs/upload-file-multiple/")]
		[ResponseCache(NoStore = true, Duration = 0)]
		[RequestSizeLimit(104_857_600)] // 100 MB upload limit to prevent DoS (CWE-400)
		public IActionResult UploadFileMultiple([FromForm] List<IFormFile> files)
		{
			var resultRecords = new List<EntityRecord>();
			var response = new ResponseModel { Timestamp = DateTime.UtcNow, Success = true, Errors = new List<ErrorModel>() };

			using (var connection = CoreDbContext.Current.CreateConnection())
			{
				connection.BeginTransaction();

				try
				{
					foreach (var file in files)
					{
						var fileBuffer = ReadFully(file.OpenReadStream());
						var fileName = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.ToString().Trim().ToLowerInvariant();
						if (fileName.StartsWith("\"", StringComparison.InvariantCulture))
							fileName = fileName.Substring(1);

						if (fileName.EndsWith("\"", StringComparison.InvariantCulture))
							fileName = fileName.Substring(0, fileName.Length - 1);

						DbFile dbFile = _fileRepository.CreateTempFile(fileName, fileBuffer);

						var resultRec = new EntityRecord();

						resultRec["id"] = dbFile.Id;
						resultRec["created_on"] = DateTime.Now;
						resultRec["name"] = fileName;
						resultRec["size"] = Math.Round((decimal)(file.Length / 1024), 0);
						resultRec["path"] = dbFile.FilePath;

						var mimeType = MimeMapping.MimeUtility.GetMimeMapping(dbFile.FilePath);
						var fileExtension = Path.GetExtension(dbFile.FilePath);
						if (mimeType.StartsWith("image"))
						{
							var dimensionsRecord = Helpers.GetImageDimension(fileBuffer);
							resultRec["width"] = (decimal)dimensionsRecord["width"];
							resultRec["height"] = (decimal)dimensionsRecord["height"];
							resultRec["type"] = "image";
						}
						else if (mimeType.StartsWith("video"))
						{
							resultRec["type"] = "video";
						}
						else if (mimeType.StartsWith("audio"))
						{
							resultRec["type"] = "audio";
						}
						else if (fileExtension == ".doc" || fileExtension == ".docx" || fileExtension == ".odt" || fileExtension == ".rtf"
						 || fileExtension == ".txt" || fileExtension == ".pdf" || fileExtension == ".html" || fileExtension == ".htm" || fileExtension == ".ppt"
						  || fileExtension == ".pptx" || fileExtension == ".xls" || fileExtension == ".xlsx" || fileExtension == ".ods" || fileExtension == ".odp")
						{
							resultRec["type"] = "document";
						}
						else
						{
							resultRec["type"] = "other";
						}

						resultRecords.Add(resultRec);
					}

					connection.CommitTransaction();
					response.Success = true;
					response.Object = resultRecords;
					return DoResponse(response);
				}
				catch (Exception ex)
				{
					connection.RollbackTransaction();
					response.Success = false;
					response.Message = ex.Message;
					return DoResponse(response);
				}
			}
		}

		#endregion

		#region << Helper Methods >>

		/// <summary>
		/// Reads the entire contents of a stream into a byte array.
		/// Preserved from monolith WebApiController.ReadFully (lines 3385-3397).
		/// </summary>
		/// <param name="input">The input stream to read.</param>
		/// <returns>Byte array containing all bytes from the stream.</returns>
		private static byte[] ReadFully(Stream input)
		{
			byte[] buffer = new byte[16 * 1024];
			using (MemoryStream ms = new MemoryStream())
			{
				int read;
				while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
				{
					ms.Write(buffer, 0, read);
				}
				return ms.ToArray();
			}
		}

		/// <summary>
		/// Generates a readable stream from a string value.
		/// Preserved from monolith WebApiController.GenerateStreamFromString (lines 4221-4229).
		/// </summary>
		/// <param name="s">The string to convert to a stream.</param>
		/// <returns>A MemoryStream containing the string content.</returns>
		public static Stream GenerateStreamFromString(string s)
		{
			var stream = new MemoryStream();
			var writer = new StreamWriter(stream);
			writer.Write(s);
			writer.Flush();
			stream.Position = 0;
			return stream;
		}

		/// <summary>
		/// Internal helper that creates a user_file record from an existing file path.
		/// Inlines the monolith's UserFileService.CreateUserFile logic for use by
		/// CKEditor upload endpoints.
		/// </summary>
		/// <param name="path">The file path (may start with "/fs").</param>
		/// <param name="alt">Optional alt text for the file.</param>
		/// <param name="caption">Optional caption for the file.</param>
		/// <returns>An EntityRecord with "path" property set to the new file location.</returns>
		private EntityRecord CreateUserFileInternal(string path, string alt, string caption)
		{
			var userFileRecord = new EntityRecord();
			if (path.StartsWith("/fs"))
			{
				path = path.Substring(3);
			}
			var tempFile = _fileRepository.Find(path);
			if (tempFile == null)
			{
				throw new Exception("File not found on that path");
			}
			var newFileId = Guid.NewGuid();
			userFileRecord["id"] = newFileId;
			userFileRecord["alt"] = alt;
			userFileRecord["caption"] = caption;
			var fileKilobytes = Math.Round(((decimal)tempFile.GetBytes().Length / 1024), 2);
			userFileRecord["size"] = fileKilobytes;
			userFileRecord["name"] = Path.GetFileName(path);
			var fileExtension = Path.GetExtension(path);
			var mimeType = MimeMapping.MimeUtility.GetMimeMapping(path);
			if (mimeType.StartsWith("image"))
			{
				var dimensionsRecord = Helpers.GetImageDimension(tempFile.GetBytes());
				userFileRecord["width"] = (decimal)dimensionsRecord["width"];
				userFileRecord["height"] = (decimal)dimensionsRecord["height"];
				userFileRecord["type"] = "image";
			}
			else if (mimeType.StartsWith("video"))
			{
				userFileRecord["type"] = "video";
			}
			else if (mimeType.StartsWith("audio"))
			{
				userFileRecord["type"] = "audio";
			}
			else if (fileExtension == ".doc" || fileExtension == ".docx" || fileExtension == ".odt" || fileExtension == ".rtf"
			 || fileExtension == ".txt" || fileExtension == ".pdf" || fileExtension == ".html" || fileExtension == ".htm" || fileExtension == ".ppt"
			  || fileExtension == ".pptx" || fileExtension == ".xls" || fileExtension == ".xlsx" || fileExtension == ".ods" || fileExtension == ".odp")
			{
				userFileRecord["type"] = "document";
			}
			else
			{
				userFileRecord["type"] = "other";
			}

			var newFilePath = $"/file/{newFileId}/{Path.GetFileName(path)}";

			using (DbConnection con = CoreDbContext.Current.CreateConnection())
			{
				con.BeginTransaction();
				try
				{
					var file = _fileRepository.Move(path, newFilePath, false);
					if (file == null)
					{
						throw new Exception("File move from temp folder failed");
					}

					userFileRecord["path"] = newFilePath;
					var createResponse = _recordManager.CreateRecord("user_file", userFileRecord);
					if (!createResponse.Success)
						throw new Exception(createResponse.Message);

					userFileRecord = createResponse.Object.Data.First();
					con.CommitTransaction();
				}
				catch (Exception)
				{
					con.RollbackTransaction();
					throw;
				}
			}
			return userFileRecord;
		}

		#endregion

		#region << Response Models >>

		/// <summary>
		/// File system response model wrapping an FSResult.
		/// Preserved from monolith WebVella.Erp.Api.Models.FSResponse.
		/// </summary>
		public class FSResponse : BaseResponseModel
		{
			[JsonProperty(PropertyName = "object")]
			public FSResult Object { get; set; }

			public FSResponse()
			{
				Timestamp = DateTime.UtcNow;
				Success = true;
			}

			public FSResponse(FSResult result) : this()
			{
				Object = result;
			}
		}

		/// <summary>
		/// File system result containing URL and filename.
		/// Preserved from monolith WebVella.Erp.Api.Models.FSResult.
		/// </summary>
		public class FSResult
		{
			[JsonProperty(PropertyName = "url")]
			public string Url { get; set; }

			[JsonProperty(PropertyName = "filename")]
			public string Filename { get; set; }
		}

		#endregion
	}
}
