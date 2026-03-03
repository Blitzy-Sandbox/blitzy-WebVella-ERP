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
	/// - ErpSettings imported from SharedKernel
	/// </summary>
	public class DbFileRepository
	{
		/// <summary>
		/// Forward-slash folder separator used in file path normalization.
		/// </summary>
		public const string FOLDER_SEPARATOR = "/";

		/// <summary>
		/// Name of the temporary file folder.
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
		/// <param name="currentContext">Optional CoreDbContext injection. Falls back to CoreDbContext.Current.</param>
		public DbFileRepository(CoreDbContext currentContext = null)
		{
			if (currentContext != null)
				suppliedContext = currentContext;
		}

		/// <summary>
		/// Finds a single file by its normalized filepath.
		/// </summary>
		/// <param name="filepath">Logical file path (will be lowercased and separator-prefixed).</param>
		/// <returns>DbFile instance, or null if not found.</returns>
		public DbFile Find(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			//all filepaths are lowercase and all starts with folder separator
			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			DataTable table = new DataTable();
			using (DbConnection con = CurrentContext.CreateConnection())
			{
				NpgsqlCommand command = con.CreateCommand("SELECT * FROM files WHERE filepath = @filepath");
				command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
				new NpgsqlDataAdapter(command).Fill(table);
			}

			if (table.Rows.Count == 0)
				return null;

			return new DbFile(table.Rows[0]);
		}

		/// <summary>
		/// Returns all files matching the specified path prefix, with optional temp file filtering and pagination.
		/// </summary>
		/// <param name="startsWithPath">Path prefix filter (null for all files).</param>
		/// <param name="includeTempFiles">Whether to include files in the tmp folder.</param>
		/// <param name="skip">Number of records to skip (pagination).</param>
		/// <param name="limit">Maximum records to return (pagination).</param>
		/// <returns>List of matching DbFile instances.</returns>
		public List<DbFile> FindAll(string startsWithPath, bool includeTempFiles, int? skip, int? limit)
		{
			DataTable table = new DataTable();
			using (DbConnection con = CurrentContext.CreateConnection())
			{
				string sql;
				if (string.IsNullOrWhiteSpace(startsWithPath))
				{
					if (includeTempFiles)
						sql = "SELECT * FROM files ORDER BY filepath";
					else
						sql = $"SELECT * FROM files WHERE filepath NOT ILIKE '/{TMP_FOLDER_NAME}/%' ORDER BY filepath";
				}
				else
				{
					startsWithPath = startsWithPath.ToLowerInvariant();
					if (!startsWithPath.StartsWith(FOLDER_SEPARATOR))
						startsWithPath = FOLDER_SEPARATOR + startsWithPath;

					if (includeTempFiles)
						sql = $"SELECT * FROM files WHERE filepath ILIKE '{startsWithPath}%' ORDER BY filepath";
					else
						sql = $"SELECT * FROM files WHERE filepath ILIKE '{startsWithPath}%' AND filepath NOT ILIKE '/{TMP_FOLDER_NAME}/%' ORDER BY filepath";
				}

				if (limit != null && limit.Value > 0)
				{
					sql += $" LIMIT {limit.Value}";
					if (skip != null && skip.Value > 0)
						sql += $" OFFSET {skip.Value}";
				}
				else
				{
					sql += " LIMIT ALL";
					if (skip != null && skip.Value > 0)
						sql += $" OFFSET {skip.Value}";
				}

				NpgsqlCommand command = con.CreateCommand(sql);
				new NpgsqlDataAdapter(command).Fill(table);
			}

			List<DbFile> files = new List<DbFile>();
			foreach (DataRow row in table.Rows)
				files.Add(new DbFile(row));

			return files;
		}

		/// <summary>
		/// Creates a new file record with content stored across the configured storage backend.
		/// Three storage backends: PostgreSQL Large Object, filesystem, and cloud blob (Storage.Net).
		/// </summary>
		/// <param name="filepath">Logical file path (normalized to lowercase).</param>
		/// <param name="buffer">File content bytes.</param>
		/// <param name="createdOn">Optional creation timestamp (defaults to DateTime.UtcNow).</param>
		/// <param name="createdBy">Optional creator user GUID.</param>
		/// <returns>The created DbFile instance.</returns>
		public DbFile Create(string filepath, byte[] buffer, DateTime? createdOn = null, Guid? createdBy = null)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			if (createdOn == null)
				createdOn = DateTime.UtcNow;

			Guid id = Guid.NewGuid();
			uint objectId = 0;

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				try
				{
					con.BeginTransaction();

					if (ErpSettings.EnableCloudBlobStorage)
					{
						var tmpFile = new DbFile { Id = id, FilePath = filepath };
						var blobPath = GetBlobPath(tmpFile);
						using (IBlobStorage storage = GetBlobStorage())
						{
							storage.WriteAsync(blobPath, new MemoryStream(buffer)).Wait();
						}
					}
					else if (ErpSettings.EnableFileSystemStorage)
					{
						var tmpFile = new DbFile { Id = id, FilePath = filepath };
						var filePath = GetFileSystemPath(tmpFile);
						var directoryPath = Path.GetDirectoryName(filePath);
						if (!string.IsNullOrWhiteSpace(directoryPath))
							new DirectoryInfo(directoryPath).Create();
						using (var stream = File.Open(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
						{
							stream.Write(buffer, 0, buffer.Length);
						}
					}
					else
					{
						var manager = new NpgsqlLargeObjectManager(con.connection);
						objectId = manager.Create();
						using (var stream = manager.OpenReadWrite(objectId))
						{
							stream.Write(buffer, 0, buffer.Length);
						}
					}

					var command = con.CreateCommand("");
					command.CommandText = "INSERT INTO files (id, object_id, filepath, created_on, modified_on, created_by, modified_by) " +
										  "VALUES (@id, @object_id, @filepath, @created_on, @modified_on, @created_by, @modified_by)";
					command.Parameters.Add(new NpgsqlParameter("@id", id));
					command.Parameters.Add(new NpgsqlParameter("@object_id", (decimal)objectId));
					command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
					command.Parameters.Add(new NpgsqlParameter("@created_on", createdOn.Value));
					command.Parameters.Add(new NpgsqlParameter("@modified_on", createdOn.Value));
					command.Parameters.Add(new NpgsqlParameter("@created_by", (object)createdBy ?? DBNull.Value));
					command.Parameters.Add(new NpgsqlParameter("@modified_by", (object)createdBy ?? DBNull.Value));
					command.ExecuteNonQuery();

					con.CommitTransaction();
				}
				catch
				{
					con.RollbackTransaction();
					throw;
				}
			}

			return Find(filepath);
		}

		/// <summary>
		/// Updates the modification date of a file by filepath.
		/// </summary>
		/// <param name="filepath">Logical file path.</param>
		/// <param name="modificationDate">New modification timestamp.</param>
		/// <returns>Updated DbFile instance.</returns>
		public DbFile UpdateModificationDate(string filepath, DateTime modificationDate)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				NpgsqlCommand command = con.CreateCommand("UPDATE files SET modified_on = @modified_on WHERE filepath = @filepath");
				command.Parameters.Add(new NpgsqlParameter("@modified_on", modificationDate));
				command.Parameters.Add(new NpgsqlParameter("@filepath", filepath));
				command.ExecuteNonQuery();
			}

			return Find(filepath);
		}

		/// <summary>
		/// Copies a file from source to destination path. Reads bytes from source and creates a new file.
		/// </summary>
		/// <param name="sourceFilepath">Source file path.</param>
		/// <param name="destinationFilepath">Destination file path.</param>
		/// <param name="overwrite">If true, overwrites existing destination file.</param>
		/// <returns>The newly created DbFile at the destination path.</returns>
		public DbFile Copy(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");
			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			destinationFilepath = destinationFilepath.ToLowerInvariant();
			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var existingSource = Find(sourceFilepath);
			if (existingSource == null)
				throw new Exception($"Source file '{sourceFilepath}' was not found.");

			var existingDest = Find(destinationFilepath);
			if (existingDest != null)
			{
				if (overwrite)
					Delete(destinationFilepath);
				else
					throw new Exception($"Destination file '{destinationFilepath}' already exists.");
			}

			var bytes = existingSource.GetBytes();
			return Create(destinationFilepath, bytes, existingSource.CreatedOn, existingSource.CreatedBy);
		}

		/// <summary>
		/// Moves a file from source to destination path by updating the filepath in the database
		/// and moving the physical file on the storage backend.
		/// </summary>
		/// <param name="sourceFilepath">Source file path.</param>
		/// <param name="destinationFilepath">Destination file path.</param>
		/// <param name="overwrite">If true, overwrites existing destination file.</param>
		/// <returns>The moved DbFile at the destination path.</returns>
		public DbFile Move(string sourceFilepath, string destinationFilepath, bool overwrite = false)
		{
			if (string.IsNullOrWhiteSpace(sourceFilepath))
				throw new ArgumentException("sourceFilepath cannot be null or empty");
			if (string.IsNullOrWhiteSpace(destinationFilepath))
				throw new ArgumentException("destinationFilepath cannot be null or empty");

			sourceFilepath = sourceFilepath.ToLowerInvariant();
			if (!sourceFilepath.StartsWith(FOLDER_SEPARATOR))
				sourceFilepath = FOLDER_SEPARATOR + sourceFilepath;

			destinationFilepath = destinationFilepath.ToLowerInvariant();
			if (!destinationFilepath.StartsWith(FOLDER_SEPARATOR))
				destinationFilepath = FOLDER_SEPARATOR + destinationFilepath;

			var existingSource = Find(sourceFilepath);
			if (existingSource == null)
				throw new Exception($"Source file '{sourceFilepath}' was not found.");

			var existingDest = Find(destinationFilepath);
			if (existingDest != null)
			{
				if (overwrite)
					Delete(destinationFilepath);
				else
					throw new Exception($"Destination file '{destinationFilepath}' already exists.");
			}

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				try
				{
					con.BeginTransaction();

					NpgsqlCommand command = con.CreateCommand("UPDATE files SET filepath = @destination_filepath WHERE filepath = @source_filepath");
					command.Parameters.Add(new NpgsqlParameter("@destination_filepath", destinationFilepath));
					command.Parameters.Add(new NpgsqlParameter("@source_filepath", sourceFilepath));
					command.ExecuteNonQuery();

					if (ErpSettings.EnableCloudBlobStorage && existingSource.ObjectId == 0)
					{
						var sourceBlob = GetBlobPath(existingSource);
						var destFile = new DbFile { Id = existingSource.Id, FilePath = destinationFilepath };
						var destBlob = GetBlobPath(destFile);
						using (IBlobStorage storage = GetBlobStorage())
						{
							var bytes = storage.OpenReadAsync(sourceBlob).Result;
							using (var ms = new MemoryStream())
							{
								bytes.CopyTo(ms);
								ms.Position = 0;
								storage.WriteAsync(destBlob, ms).Wait();
							}
							storage.DeleteAsync(new[] { sourceBlob }).Wait();
						}
					}
					else if (ErpSettings.EnableFileSystemStorage && existingSource.ObjectId == 0)
					{
						var sourceFsPath = GetFileSystemPath(existingSource);
						var destFile = new DbFile { Id = existingSource.Id, FilePath = destinationFilepath };
						var destFsPath = GetFileSystemPath(destFile);
						var directoryPath = Path.GetDirectoryName(destFsPath);
						if (!string.IsNullOrWhiteSpace(directoryPath))
							new DirectoryInfo(directoryPath).Create();
						if (File.Exists(sourceFsPath))
							new FileInfo(sourceFsPath).MoveTo(destFsPath);
					}

					con.CommitTransaction();
				}
				catch
				{
					con.RollbackTransaction();
					throw;
				}
			}

			return Find(destinationFilepath);
		}

		/// <summary>
		/// Deletes a file by its filepath, cleaning up the physical storage backend.
		/// Returns silently if the file is not found.
		/// </summary>
		/// <param name="filepath">Logical file path to delete.</param>
		public void Delete(string filepath)
		{
			if (string.IsNullOrWhiteSpace(filepath))
				throw new ArgumentException("filepath cannot be null or empty");

			filepath = filepath.ToLowerInvariant();
			if (!filepath.StartsWith(FOLDER_SEPARATOR))
				filepath = FOLDER_SEPARATOR + filepath;

			var file = Find(filepath);
			if (file == null)
				return;

			using (DbConnection con = CurrentContext.CreateConnection())
			{
				try
				{
					con.BeginTransaction();

					if (ErpSettings.EnableCloudBlobStorage && file.ObjectId == 0)
					{
						var blobPath = GetBlobPath(file);
						using (IBlobStorage storage = GetBlobStorage())
						{
							storage.DeleteAsync(new[] { blobPath }).Wait();
						}
					}
					else if (ErpSettings.EnableFileSystemStorage && file.ObjectId == 0)
					{
						var fsPath = GetFileSystemPath(file);
						if (File.Exists(fsPath))
							File.Delete(fsPath);
					}
					else
					{
						if (file.ObjectId != 0)
						{
							var manager = new NpgsqlLargeObjectManager(con.connection);
							manager.Unlink(file.ObjectId);
						}
					}

					NpgsqlCommand command = con.CreateCommand("DELETE FROM files WHERE id = @id");
					command.Parameters.Add(new NpgsqlParameter("@id", file.Id));
					command.ExecuteNonQuery();

					con.CommitTransaction();
				}
				catch
				{
					con.RollbackTransaction();
					throw;
				}
			}
		}

		/// <summary>
		/// Creates a temporary file in the /tmp/{guid}/ folder.
		/// </summary>
		/// <param name="filename">Base filename.</param>
		/// <param name="buffer">File content bytes.</param>
		/// <param name="extension">Optional file extension override.</param>
		/// <returns>The created temporary DbFile.</returns>
		public DbFile CreateTempFile(string filename, byte[] buffer, string extension = null)
		{
			if (string.IsNullOrWhiteSpace(filename))
				throw new ArgumentException("filename cannot be null or empty");

			if (!string.IsNullOrWhiteSpace(extension))
			{
				if (!extension.StartsWith("."))
					extension = "." + extension;
				filename = Path.GetFileNameWithoutExtension(filename) + extension;
			}

			var filepath = FOLDER_SEPARATOR + TMP_FOLDER_NAME + FOLDER_SEPARATOR + Guid.NewGuid() + FOLDER_SEPARATOR + filename;
			return Create(filepath, buffer);
		}

		/// <summary>
		/// Cleans up expired temporary files. NOTE: The expiration parameter is NOT used in the
		/// monolith implementation — this behavior is intentionally preserved.
		/// </summary>
		/// <param name="expiration">Expiration timespan (NOT USED — preserved from monolith).</param>
		public void CleanupExpiredTempFiles(TimeSpan expiration)
		{
			DataTable table = new DataTable();
			using (DbConnection con = CurrentContext.CreateConnection())
			{
				NpgsqlCommand command = con.CreateCommand($"SELECT * FROM files WHERE filepath ILIKE '/{TMP_FOLDER_NAME}/%'");
				new NpgsqlDataAdapter(command).Fill(table);
			}

			foreach (DataRow row in table.Rows)
				Delete((string)row["filepath"]);
		}

		/// <summary>
		/// Creates an IBlobStorage instance for cloud blob storage operations.
		/// Uses Storage.Net StorageFactory with the configured connection string.
		/// </summary>
		/// <param name="overrideConnectionString">Optional override connection string.</param>
		/// <returns>IBlobStorage instance for read/write/delete operations.</returns>
		internal static IBlobStorage GetBlobStorage(string overrideConnectionString = null)
		{
			return StorageFactory.Blobs.FromConnectionString(string.IsNullOrWhiteSpace(overrideConnectionString) ? ErpSettings.CloudBlobStorageConnectionString : overrideConnectionString);
		}

		/// <summary>
		/// Computes the filesystem path for a file using GUID-sharded directory structure.
		/// Uses 2-character depth folders from the first segment of the file GUID.
		///
		/// KNOWN BUG (preserved from monolith): Path.GetExtension() returns ".ext" WITH the dot,
		/// but the code adds "." + prefix = double dot. This is intentionally preserved to maintain
		/// backward compatibility with existing file storage layouts.
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
