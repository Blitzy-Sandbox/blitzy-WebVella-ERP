using Npgsql;
using Storage.Net;
using Storage.Net.Blobs;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// End-to-end file lifecycle management (Find/FindAll/Create/Copy/Move/Delete/CreateTempFile/
	/// CleanupExpiredTempFiles/UpdateModificationDate) coordinating the <c>files</c> table and three
	/// storage backends: PostgreSQL Large Objects, filesystem, and cloud blobs (Storage.Net/S3 via LocalStack).
	/// Scoped to Core service database (<c>erp_core</c>).
	///
	/// Migrated from monolith WebVella.Erp.Database.DbFileRepository with the following changes:
	/// - Namespace: WebVella.Erp.Database → WebVella.Erp.Service.Core.Database
	/// - DbContext → CoreDbContext for per-service ambient context
	/// - ErpSettings imported from WebVella.Erp.SharedKernel
	/// - DbConnection imported from WebVella.Erp.SharedKernel.Database
	///
	/// All business logic, SQL queries, storage backend branching, transaction handling,
	/// and known bugs are preserved byte-for-byte from the monolith source.
	/// </summary>
	public class DbFileRepository
	{
		/// <summary>
		/// Forward-slash folder separator used in all file path normalization.
		/// </summary>
		public const string FOLDER_SEPARATOR = "/";

		/// <summary>
		/// Name of the temporary file folder used by CreateTempFile and CleanupExpiredTempFiles.
		/// </summary>
		public const string TMP_FOLDER_NAME = "tmp";

		private CoreDbContext suppliedContext = null;
		private CoreDbContext CurrentContext
		{
			get
			{
				if (suppliedContext != null)
					return suppliedContext;
				else
					return CoreDbContext.Current;
			}
		}

		/// <summary>
		/// Creates a new DbFileRepository instance.
		/// </summary>
		/// <param name="currentContext">Optional CoreDbContext injection. Falls back to CoreDbContext.Current when null.</param>
		public DbFileRepository(CoreDbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Finds a single file by its normalized filepath.
		/// All filepaths are lowercased and prefixed with the folder separator before querying.
		/// </summary>
		/// <param name="filepath">Logical file path (will be lowercased and separator-prefixed).</param>
		/// <returns>DbFile instance if found, or null if no matching file exists.</returns>
		public DbFile Find(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			using (var connection = CurrentContext.CreateConnection())
			{
				var command = connection.CreateCommand("SELECT * FROM files WHERE filepath = @filepath ");
				command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
				DataTable dataTable = new DataTable();
				new NpgsqlDataAdapter(command).Fill(dataTable);

				if (dataTable.Rows.Count == 1)
					return new DbFile(dataTable.Rows[0]);
			}

			return null;
		}

		/// <summary>
		/// Returns all files matching the specified path prefix, with optional temp file filtering and pagination.
		/// Four SQL branches: with/without temp filter × with/without path filter.
		/// Uses ILIKE for case-insensitive pattern matching and parameterized queries for safety.
		/// </summary>
		/// <param name="startsWithPath">Path prefix filter (null for all files). Default: null.</param>
		/// <param name="includeTempFiles">Whether to include files in the tmp folder. Default: false.</param>
		/// <param name="skip">Number of records to skip (pagination). Default: null.</param>
		/// <param name="limit">Maximum records to return (pagination). Default: null.</param>
		/// <returns>List of matching DbFile instances.</returns>
		public List<DbFile> FindAll(string startsWithPath = null, bool includeTempFiles = false, int? skip = null, int? limit = null)
		{
			//all filepaths are lowercase and all starts with folder separator
			if (!string.IsNullOrWhiteSpace(startsWithPath))
			{
				startsWithPath = startsWithPath.ToLowerInvariant();

				if (!startsWithPath.StartsWith(FOLDER_SEPARATOR))
					startsWithPath = FOLDER_SEPARATOR + startsWithPath;
			}

			string pagingSql = string.Empty;
			if (limit != null || skip != null)
			{
				pagingSql = " LIMIT ";
				if (limit.HasValue)
					pagingSql = pagingSql + limit + " ";
				else
					pagingSql = pagingSql + "ALL ";

				if (skip.HasValue)
					pagingSql = pagingSql + " OFFSET " + skip;
			}

			DataTable table = new DataTable();
			using (var connection = CurrentContext.CreateConnection())
			{
				var command = connection.CreateCommand(string.Empty);
				if (!includeTempFiles && !string.IsNullOrWhiteSpace(startsWithPath))
				{
					command.CommandText = "SELECT * FROM files WHERE filepath NOT ILIKE @tmp_path AND filepath ILIKE @startswith" + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
					command.Parameters.Add(new NpgsqlParameter("@startswith", "%" + startsWithPath));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else if (!string.IsNullOrWhiteSpace(startsWithPath))
				{
					command.CommandText = "SELECT * FROM files WHERE filepath ILIKE @startswith" + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@startswith", "%" + startsWithPath));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else if (!includeTempFiles)
				{
					command.CommandText = "SELECT * FROM files WHERE filepath NOT ILIKE @tmp_path " + pagingSql;
					command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
					new NpgsqlDataAdapter(command).Fill(table);
				}
				else
				{
					command.CommandText = "SELECT * FROM files " + pagingSql;
					new NpgsqlDataAdapter(command).Fill(table);
				}
			}

			List<DbFile> files = new List<DbFile>();
			foreach (DataRow row in table.Rows)
				files.Add(new DbFile(row));

			return files;
		}

		/// <summary>
		/// Creates a new file record with content stored across the configured storage backend.
		/// Three storage backends in order: PostgreSQL Large Object (default), cloud blob, filesystem.
		/// The LO write happens BEFORE the INSERT; cloud/FS writes happen AFTER the INSERT.
		/// Wrapped in a transaction with rollback on failure.
		/// </summary>
		/// <param name="filepath">Logical file path (normalized to lowercase with separator prefix).</param>
		/// <param name="buffer">File content bytes.</param>
		/// <param name="createdOn">Optional creation timestamp (defaults to DateTime.UtcNow).</param>
		/// <param name="createdBy">Optional creator user GUID.</param>
		/// <returns>The created DbFile instance (re-queried after commit).</returns>
		public DbFile Create(string filepath, byte[] buffer, DateTime? createdOn, Guid? createdBy)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			if (Find(filepath) != null)
				throw new ArgumentException(filepath + ": file already exists");

			using (var connection = CurrentContext.CreateConnection())
			{
				try
				{
					uint objectId = 0;
					connection.BeginTransaction();

					if (!ErpSettings.EnableFileSystemStorage)
					{
						var manager = new NpgsqlLargeObjectManager(connection.connection);
						objectId = manager.Create();

						using (var stream = manager.OpenReadWrite(objectId))
						{
							stream.Write(buffer, 0, buffer.Length);
							stream.Close();
						}
					}


					var command = connection.CreateCommand(@"INSERT INTO files(id,object_id,filepath,created_on,modified_on,created_by,modified_by) 
															 VALUES (@id,@object_id,@filepath,@created_on,@modified_on,@created_by,@modified_by)");

					command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
					command.Parameters.Add(new NpgsqlParameter("@object_id", (decimal)objectId));
					command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
					var date = createdOn ?? DateTime.UtcNow;
					command.Parameters.Add(new NpgsqlParameter("@created_on", date));
					command.Parameters.Add(new NpgsqlParameter("@modified_on", date));
					command.Parameters.Add(new NpgsqlParameter("@created_by", (object)createdBy ?? DBNull.Value));
					command.Parameters.Add(new NpgsqlParameter("@modified_by", (object)createdBy ?? DBNull.Value));

					command.ExecuteNonQuery();

					var result = Find(filepath);

					if(ErpSettings.EnableCloudBlobStorage)
					{
						var path = GetBlobPath(result);
						using (IBlobStorage storage = GetBlobStorage())
						{
							storage.WriteAsync(path,
								buffer).Wait();
						}
					}
					else if (ErpSettings.EnableFileSystemStorage)
					{
						var path = GetFileSystemPath(result);
						var folderPath = Path.GetDirectoryName(path);
						if (!Directory.Exists(folderPath))
							Directory.CreateDirectory(folderPath);
						using (Stream stream = File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite))
						{
							stream.Write(buffer, 0, buffer.Length);
							stream.Close();
						}
					}

					connection.CommitTransaction();
				}
				catch (Exception)
				{
					connection.RollbackTransaction();
					throw;
				}
			}

			return Find(filepath);
		}

		/// <summary>
		/// Updates the modification date of a file by filepath.
		/// NOTE: The source implementation uses Guid.NewGuid() for the @id parameter in the WHERE clause,
		/// which means the UPDATE will not match any existing record. This behavior is preserved from the monolith.
		/// </summary>
		/// <param name="filepath">Logical file path.</param>
		/// <param name="modificationDate">New modification timestamp.</param>
		/// <returns>The DbFile instance after the update attempt.</returns>
		public DbFile UpdateModificationDate(string filepath, DateTime modificationDate)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			using (var connection = CurrentContext.CreateConnection())
			{
				var file = Find(filepath);
				if (file == null)
					throw new ArgumentException("file does not exist");

				var command = connection.CreateCommand(@"UPDATE files SET modified_on = @modified_on WHERE id = @id");
				command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()));
				command.Parameters.Add(new NpgsqlParameter("@modified_on", modificationDate));
				command.ExecuteNonQuery();

				return Find(filepath);
			}
		}

		/// <summary>
		/// Copies a file from source to destination location.
		/// Reads bytes from source file using the current connection, creates a new file at destination.
		/// Wrapped in a transaction with rollback on failure.
		/// </summary>
		/// <param name="sourceFilepath">Source file path.</param>
		/// <param name="destinationFilepath">Destination file path.</param>
		/// <param name="overwrite">If true, deletes existing destination file before copying.</param>
		/// <returns>The newly created DbFile at the destination path.</returns>
		public DbFile Copy(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");

			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			destinationFilepath = destinationFilepath.ToLowerInvariant();

			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var srcFile = Find(sourceFilepath);
			var destFile = Find(destinationFilepath);

			if (srcFile == null)
				throw new Exception("Source file cannot be found.");

			if (destFile != null && overwrite == false)
				throw new Exception("Destination file already exists and no overwrite specified.");

			using (var connection = CurrentContext.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					if (destFile != null && overwrite)
						Delete(destFile.FilePath);

					var bytes = srcFile.GetBytes(connection);
					var newFile = Create(destinationFilepath, bytes, srcFile.CreatedOn, srcFile.CreatedBy);

					connection.CommitTransaction();
					return newFile;
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}

		/// <summary>
		/// Moves a file from source to destination location by updating the filepath in the database
		/// and moving the physical file on the storage backend.
		/// For cloud blob: reads from source path, writes to destination path, deletes source.
		/// For filesystem: only moves if the filename portion changed.
		/// Wrapped in a transaction with rollback on failure.
		/// </summary>
		/// <param name="sourceFilepath">Source file path.</param>
		/// <param name="destinationFilepath">Destination file path.</param>
		/// <param name="overwrite">If true, deletes existing destination file before moving.</param>
		/// <returns>The moved DbFile at the destination path.</returns>
		public DbFile Move(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");

			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			destinationFilepath = destinationFilepath.ToLowerInvariant();

			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var srcFile = Find(sourceFilepath);
			var destFile = Find(destinationFilepath);

			if (srcFile == null)
				throw new Exception("Source file cannot be found.");

			if (destFile != null && overwrite == false)
				throw new Exception("Destination file already exists and no overwrite specified.");

			using (var connection = CurrentContext.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();

					if (destFile != null && overwrite)
						Delete(destFile.FilePath);

					var command = connection.CreateCommand(@"UPDATE files SET filepath = @filepath WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", srcFile.Id));
					command.Parameters.Add(new NpgsqlParameter("@filepath", destinationFilepath));
					command.ExecuteNonQuery();
					if(ErpSettings.EnableCloudBlobStorage)
					{
						var srcPath = StoragePath.Combine(StoragePath.RootFolderPath, sourceFilepath);
						var destinationPath = StoragePath.Combine(StoragePath.RootFolderPath, destinationFilepath);
						using (IBlobStorage storage = GetBlobStorage())
						{
							using (Stream original = storage.OpenReadAsync(srcPath).Result)
							{
								if (original != null)
								{
									storage.WriteAsync(destinationPath, original).Wait();
									storage.DeleteAsync(sourceFilepath).Wait();
								}
							}

						}
					} 
					else if (ErpSettings.EnableFileSystemStorage)
					{
						var srcFileName = Path.GetFileName(sourceFilepath);
						var destFileName = Path.GetFileName(destinationFilepath);
						if (srcFileName != destFileName)
						{
							var fsSrcFilePath = GetFileSystemPath(srcFile);
							srcFile.FilePath = destinationFilepath;
							var fsDestFilePath = GetFileSystemPath(srcFile);
							File.Move(fsSrcFilePath, fsDestFilePath);
						}
					}

					connection.CommitTransaction();
					return Find(destinationFilepath);
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}


		/// <summary>
		/// Deletes a file by its filepath, cleaning up the physical storage backend.
		/// For cloud blob: checks existence before deleting.
		/// For filesystem: checks File.Exists before deleting.
		/// For LO: unlinks the large object if ObjectId != 0.
		/// Returns silently if the file is not found.
		/// </summary>
		/// <param name="filepath">Logical file path to delete.</param>
		public void Delete(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			var file = Find(filepath);

			if (file == null)
				return;

			using (var connection = CurrentContext.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					if(ErpSettings.EnableCloudBlobStorage && file.ObjectId == 0)
					{
						var path = GetBlobPath(file);
						using (IBlobStorage storage = GetBlobStorage())
						{
							if (storage.ExistsAsync(path).Result)
							{
								storage.DeleteAsync(path).Wait();
							}
						}
					} else if (ErpSettings.EnableFileSystemStorage && file.ObjectId == 0)
					{
						var path = GetFileSystemPath(file);
						if( File.Exists(path))
							File.Delete(path);
					}
					else
					{
						if( file.ObjectId != 0 )
							new NpgsqlLargeObjectManager(connection.connection).Unlink(file.ObjectId);
					}

					var command = connection.CreateCommand(@"DELETE FROM files WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", file.Id));
					command.ExecuteNonQuery();

					connection.CommitTransaction();
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}

		/// <summary>
		/// Creates a temporary file in the /tmp/{section}/ folder where section is a GUID without dashes.
		/// The extension is trimmed, lowercased, and dot-prefixed before appending to the filename.
		/// </summary>
		/// <param name="filename">Base filename for the temp file.</param>
		/// <param name="buffer">File content bytes.</param>
		/// <param name="extension">Optional file extension override. Default: null.</param>
		/// <returns>The created temporary DbFile.</returns>
		public DbFile CreateTempFile(string filename, byte[] buffer, string extension = null)
		{
			if (!string.IsNullOrWhiteSpace(extension))
			{
				extension = extension.Trim().ToLowerInvariant();
				if (!extension.StartsWith("."))
					extension = "." + extension;
			}

			string section = Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant();
			var tmpFilePath = FOLDER_SEPARATOR + TMP_FOLDER_NAME + FOLDER_SEPARATOR + section + FOLDER_SEPARATOR + filename + extension ?? string.Empty;
			return Create(tmpFilePath, buffer, DateTime.UtcNow, null);
		}

		/// <summary>
		/// Cleans up expired temporary files by finding all files with paths matching the tmp folder pattern
		/// and deleting each one. NOTE: The expiration parameter is NOT used in the implementation —
		/// this behavior is intentionally preserved from the monolith source.
		/// </summary>
		/// <param name="expiration">Expiration timespan (NOT USED — preserved from monolith).</param>
		public void CleanupExpiredTempFiles(TimeSpan expiration)
		{

			DataTable table = new DataTable();
			using (var connection = CurrentContext.CreateConnection())
			{
				var command = connection.CreateCommand(string.Empty);
				command.CommandText = "SELECT filepath FROM files WHERE filepath ILIKE @tmp_path";
				command.Parameters.Add(new NpgsqlParameter("@tmp_path", "%" + FOLDER_SEPARATOR + TMP_FOLDER_NAME));
				new NpgsqlDataAdapter(command).Fill(table);
			}

			foreach (DataRow row in table.Rows)
				Delete((string)row["filepath"]);
		}

		/// <summary>
		/// Creates an IBlobStorage instance for cloud blob storage operations.
		/// Uses Storage.Net StorageFactory with the configured connection string from ErpSettings,
		/// or an override connection string if provided.
		/// </summary>
		/// <param name="overrideConnectionString">Optional override connection string. Default: null (uses ErpSettings).</param>
		/// <returns>IBlobStorage instance for read/write/delete operations.</returns>
		internal static IBlobStorage GetBlobStorage(string overrideConnectionString = null)
		{
			return StorageFactory.Blobs.FromConnectionString(string.IsNullOrWhiteSpace(overrideConnectionString) ? ErpSettings.CloudBlobStorageConnectionString : overrideConnectionString);
		} 

		/// <summary>
		/// Computes the filesystem path for a file using GUID-sharded directory structure.
		/// Uses first 4 characters of the GUID's first segment for 2-level depth folders.
		///
		/// KNOWN BUG (preserved from monolith): Path.GetExtension() returns ".ext" WITH the dot,
		/// but the code adds "." + prefix = double dot. This is intentionally preserved to maintain
		/// backward compatibility with existing file storage layouts.
		/// See: https://docs.microsoft.com/en-us/dotnet/api/system.io.path.getextension
		/// </summary>
		/// <param name="file">DbFile instance to compute path for.</param>
		/// <returns>Full filesystem path for the file.</returns>
		internal static string GetFileSystemPath(DbFile file)
		{
			var guidIinitialPart = file.Id.ToString().Split(new[] { '-' })[0];
			var fileName = file.FilePath.Split(new[] { '/' }).Last();
			var depth1Folder = guidIinitialPart.Substring(0, 2);
			var depth2Folder = guidIinitialPart.Substring(2, 2);
			// BUG: https://docs.microsoft.com/en-us/dotnet/api/system.io.path.getextension?view=net-5.0
			// Path.GetExtension includes the "." which means further on we are adding double "."
			// Would probably ruin too many databases to just fix here though
			string filenameExt = Path.GetExtension(fileName);

			if (!string.IsNullOrWhiteSpace(filenameExt))
				return Path.Combine(ErpSettings.FileSystemStorageFolder, depth1Folder, depth2Folder, file.Id + "." + filenameExt);

			else
				return Path.Combine(ErpSettings.FileSystemStorageFolder, depth1Folder, depth2Folder, file.Id.ToString());
		}


		/// <summary>
		/// Computes the cloud blob storage path for a file using GUID-sharded directory structure.
		/// Uses StoragePath.Combine for cross-platform path construction.
		/// Unlike GetFileSystemPath, this does NOT add an extra "." before the extension.
		/// </summary>
		/// <param name="file">DbFile instance to compute path for.</param>
		/// <returns>Blob storage path for the file.</returns>
		internal static string GetBlobPath(DbFile file)
		{
			var guidIinitialPart = file.Id.ToString().Split(new[] { '-' })[0];
			var fileName = file.FilePath.Split(new[] { '/' }).Last();
			var depth1Folder = guidIinitialPart.Substring(0, 2);
			var depth2Folder = guidIinitialPart.Substring(2, 2);
			string filenameExt = Path.GetExtension(fileName);

			
			if (!string.IsNullOrWhiteSpace(filenameExt))
				return StoragePath.Combine(depth1Folder, depth2Folder, file.Id + filenameExt);
			else
				return StoragePath.Combine(depth1Folder, depth2Folder, file.Id.ToString());

		}

	}
}
