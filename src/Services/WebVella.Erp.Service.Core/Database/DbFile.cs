using Npgsql;
using Storage.Net.Blobs;
using System;
using System.Data;
using System.IO;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;

namespace WebVella.Erp.Service.Core.Database
{
	/// <summary>
	/// File metadata model class representing a single file record from the <c>files</c> table.
	/// Holds properties (Id, ObjectId, FilePath, CreatedBy, CreatedOn, LastModifiedBy, LastModificationDate)
	/// hydrated from a DataRow, and provides content access methods (GetContentStream, GetBytes) that
	/// branch across three storage backends: PostgreSQL Large Objects, filesystem, and cloud blobs.
	///
	/// Migrated from monolith WebVella.Erp.Database.DbFile with the following changes:
	/// - Namespace: WebVella.Erp.Database → WebVella.Erp.Service.Core.Database
	/// - DbContext.Current → CoreDbContext.Current in parameterless GetBytes()
	/// - Property setters changed to internal for encapsulation
	/// - Added empty internal constructor for repository use
	///
	/// Key coupling: Uses DbFileRepository static methods (GetBlobPath, GetFileSystemPath, GetBlobStorage)
	/// which are in the same namespace — this creates a mutual dependency that is valid in C#.
	/// </summary>
	public class DbFile
	{
		#region <--- Properties --->

		/// <summary>
		/// Unique identifier for the file record.
		/// </summary>
		public Guid Id { get; internal set; }

		/// <summary>
		/// PostgreSQL Large Object OID. Zero if stored on filesystem or cloud blob storage.
		/// </summary>
		public uint ObjectId { get; internal set; }

		/// <summary>
		/// Logical file path (normalized to lowercase with forward-slash separators).
		/// </summary>
		public string FilePath { get; internal set; }

		/// <summary>
		/// GUID of the user who created this file record. Null if not tracked.
		/// </summary>
		public Guid? CreatedBy { get; internal set; }

		/// <summary>
		/// Timestamp when this file record was created.
		/// </summary>
		public DateTime CreatedOn { get; internal set; }

		/// <summary>
		/// GUID of the user who last modified this file record. Null if not tracked.
		/// </summary>
		public Guid? LastModifiedBy { get; internal set; }

		/// <summary>
		/// Timestamp of the last modification to this file record.
		/// </summary>
		public DateTime LastModificationDate { get; internal set; }

		#endregion

		#region <--- Constructors --->

		/// <summary>
		/// Empty internal constructor for repository use (e.g., manual hydration).
		/// </summary>
		internal DbFile()
		{
		}

		/// <summary>
		/// DataRow-hydrating constructor. Reads file metadata from a SQL query result row.
		/// CRITICAL: ObjectId uses a double cast from decimal to uint — (uint)((decimal)row["object_id"])
		/// because PostgreSQL numeric/oid columns are returned as System.Decimal by Npgsql's DataRow adapter.
		/// </summary>
		/// <param name="row">DataRow from the files table query result.</param>
		internal DbFile(DataRow row)
		{
			Id = (Guid)row["id"];
			ObjectId = (uint)((decimal)row["object_id"]);
			FilePath = (string)row["filepath"];
			CreatedOn = (DateTime)row["created_on"];
			LastModificationDate = (DateTime)row["modified_on"];

			CreatedBy = null;
			if (row["created_by"] != DBNull.Value)
				CreatedBy = (Guid?)row["created_by"];

			LastModifiedBy = null;
			if (row["modified_by"] != DBNull.Value)
				LastModifiedBy = (Guid?)row["modified_by"];
		}

		#endregion

		#region <--- Content Access Methods --->

		/// <summary>
		/// Returns a readable Stream for the file content, branching across three storage backends:
		/// 1. Cloud blob storage (ErpSettings.EnableCloudBlobStorage) — via Storage.Net IBlobStorage
		/// 2. Filesystem (ErpSettings.EnableFileSystemStorage) — via System.IO File operations
		/// 3. PostgreSQL Large Object (default) — via NpgsqlLargeObjectManager
		///
		/// This is a private method consumed by the public GetBytes() methods.
		/// </summary>
		/// <param name="connection">DbConnection wrapper for PostgreSQL LO access. May be null for filesystem/cloud backends.</param>
		/// <param name="fileAccess">File access mode (default: ReadWrite).</param>
		/// <param name="fileShare">File sharing mode (default: ReadWrite).</param>
		/// <returns>Stream containing the file content.</returns>
		private Stream GetContentStream(DbConnection connection, FileAccess fileAccess = FileAccess.ReadWrite, FileShare fileShare = FileShare.ReadWrite)
		{
			if (ErpSettings.EnableCloudBlobStorage && this.ObjectId == 0)
			{
				var path = DbFileRepository.GetBlobPath(this);
				using (IBlobStorage storage = DbFileRepository.GetBlobStorage())
				{
					return storage.OpenReadAsync(path).Result;
				}
			}
			else if (ErpSettings.EnableFileSystemStorage && ObjectId == 0)
			{
				var path = DbFileRepository.GetFileSystemPath(this);
				if (File.Exists(path))
					return File.Open(path, FileMode.Open, fileAccess, fileShare);

				throw new Exception($"File '{path}' was not found.");
			}
			else
			{
				if (ObjectId == 0)
					throw new Exception("Trying to get content of a file from database, but it was uploaded to file system. Check FileSystem support configuration.");

				var manager = new NpgsqlLargeObjectManager(connection.connection);
				switch (fileAccess)
				{
					case FileAccess.Read:
						return manager.OpenRead(ObjectId);
					case FileAccess.Write:
						return manager.OpenRead(ObjectId);
					case FileAccess.ReadWrite:
						return manager.OpenReadWrite(ObjectId);
				}
				return manager.OpenReadWrite(ObjectId);
			}
		}

		/// <summary>
		/// Reads the file content as a byte array using the provided database connection.
		/// Branches across three storage backends via GetContentStream.
		/// For PostgreSQL Large Object access, uses connection.connection (the underlying NpgsqlConnection)
		/// to open the large object manager.
		/// </summary>
		/// <param name="connection">DbConnection wrapper providing access to the underlying NpgsqlConnection.
		/// May be null when storage is filesystem-based (no DB transaction needed).</param>
		/// <returns>Byte array of file content, or null if the content stream is empty.</returns>
		public byte[] GetBytes(DbConnection connection)
		{
			using (var contentStream = GetContentStream(connection, FileAccess.Read, FileShare.Read))
			{
				return contentStream.Length == 0 ? null : new BinaryReader(contentStream).ReadBytes((int)contentStream.Length);
			}
		}

		/// <summary>
		/// Parameterless overload that reads file content as a byte array.
		/// For filesystem storage (no DB needed), delegates directly to GetBytes(null).
		/// For PostgreSQL Large Object or cloud storage, creates a connection via
		/// CoreDbContext.Current.CreateConnection(), wraps in a transaction, and reads bytes.
		/// Replaces monolith's DbContext.Current.CreateConnection() with CoreDbContext.Current.CreateConnection().
		/// </summary>
		/// <returns>Byte array of file content, or null if the content stream is empty.</returns>
		public byte[] GetBytes()
		{
			if (ErpSettings.EnableFileSystemStorage && ObjectId == 0)
			{
				//no need for database connection and any transaction
				return GetBytes(null);
			}

			using (DbConnection connection = CoreDbContext.Current.CreateConnection())
			{
				try
				{
					connection.BeginTransaction();
					var result = GetBytes(connection);
					connection.CommitTransaction();
					return result;
				}
				catch
				{
					connection.RollbackTransaction();
					throw;
				}
			}
		}

		#endregion
	}
}
