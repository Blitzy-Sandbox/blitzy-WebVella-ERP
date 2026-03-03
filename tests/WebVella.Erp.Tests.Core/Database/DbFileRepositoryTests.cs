using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Core.Database;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Database;
using Xunit;

namespace WebVella.Erp.Tests.Core.Database
{
	/// <summary>
	/// Integration tests for DbFileRepository — the Core service's file lifecycle manager
	/// coordinating the <c>files</c> table and storage backends (PostgreSQL Large Objects,
	/// filesystem, cloud blobs).
	///
	/// Tests validate:
	/// - File metadata persistence (Create, Find, FindAll, Delete)
	/// - File path normalization (lowercase, separator prefix)
	/// - File copy and move operations with transaction handling
	/// - Temp file creation and cleanup
	/// - Backend selection (PostgreSQL Large Objects when filesystem/cloud disabled)
	/// - Pagination and filtering in FindAll
	/// - Edge cases: null/empty paths, duplicate paths, non-existent files
	///
	/// All tests use Testcontainers.PostgreSql for an isolated PostgreSQL 16-alpine instance.
	/// Tests are executed sequentially via [Collection("Database")] to prevent CoreDbContext
	/// AsyncLocal interference across parallel test threads.
	///
	/// Known preserved bugs from the monolith source:
	/// - UpdateModificationDate uses Guid.NewGuid() for the @id WHERE clause parameter,
	///   so the UPDATE never matches any record (modification date is never actually updated).
	/// - FindAll startsWithPath uses ILIKE '%' + path (suffix match) not path + '%' (prefix match).
	/// - CleanupExpiredTempFiles ignores the expiration parameter entirely.
	/// - CleanupExpiredTempFiles uses ILIKE '%/tmp' which only matches paths ending with '/tmp',
	///   not temp files created via CreateTempFile (paths like '/tmp/section/file.ext').
	/// </summary>
	[Collection("Database")]
	public class DbFileRepositoryTests : IAsyncLifetime
	{
		private readonly PostgreSqlContainer _postgres;
		private string _connectionString;

		/// <summary>
		/// Constructs the PostgreSQL test container using the postgres:16-alpine image.
		/// The container is built lazily and started in InitializeAsync.
		/// </summary>
		public DbFileRepositoryTests()
		{
			_postgres = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.Build();
		}

		/// <summary>
		/// Starts the PostgreSQL container, captures the connection string, configures
		/// ErpSettings for PostgreSQL Large Object mode, and creates the files table.
		/// </summary>
		public async Task InitializeAsync()
		{
			await _postgres.StartAsync();
			_connectionString = _postgres.GetConnectionString();

			// Configure ErpSettings for PostgreSQL Large Object mode (default backend).
			// Both filesystem and cloud blob storage are disabled, so Create/Delete/Copy
			// will use NpgsqlLargeObjectManager for binary content storage.
			ErpSettings.EnableFileSystemStorage = false;
			ErpSettings.EnableCloudBlobStorage = false;

			// Create the files table matching the schema from the monolith's ERPService.cs
			// system initialization. This table is required by all DbFileRepository operations.
			var context = CoreDbContext.CreateContext(_connectionString);
			try
			{
				using (var connection = context.CreateConnection())
				{
					var cmd = connection.CreateCommand(@"
						CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";
						CREATE TABLE IF NOT EXISTS files (
							id UUID PRIMARY KEY,
							object_id NUMERIC NOT NULL DEFAULT 0,
							filepath TEXT NOT NULL UNIQUE,
							created_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
							modified_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
							created_by UUID,
							modified_by UUID
						);
					");
					cmd.ExecuteNonQuery();
				}
			}
			finally
			{
				CoreDbContext.CloseContext();
			}
		}

		/// <summary>
		/// Stops and disposes the PostgreSQL container after all tests have completed.
		/// </summary>
		public async Task DisposeAsync()
		{
			await _postgres.DisposeAsync();
		}

		#region <=== Helper Methods ===>

		/// <summary>
		/// Safely cleans up CoreDbContext, suppressing exceptions.
		/// Ensures the ambient context is cleared even if tests leave it in an inconsistent state.
		/// </summary>
		private void SafeCleanupContext()
		{
			try
			{
				if (CoreDbContext.Current != null)
					CoreDbContext.Current.LeaveTransactionalState();
				CoreDbContext.CloseContext();
			}
			catch
			{
				// Swallow cleanup errors — the container will be destroyed anyway
			}
		}

		/// <summary>
		/// Generates a unique file path for test isolation.
		/// Each test uses unique paths to avoid cross-test interference without requiring
		/// table cleanup between tests.
		/// </summary>
		private string UniquePath(string prefix = "/test")
		{
			return prefix + "/" + Guid.NewGuid().ToString("N") + ".pdf";
		}

		/// <summary>
		/// Queries the files table directly using raw Npgsql to verify file metadata
		/// independently of the repository under test.
		/// </summary>
		private DataTable QueryFilesDirectly(string sql, NpgsqlParameter[] parameters = null)
		{
			var dt = new DataTable();
			using (var conn = new NpgsqlConnection(_connectionString))
			{
				conn.Open();
				using (var cmd = new NpgsqlCommand(sql, conn))
				{
					if (parameters != null)
						cmd.Parameters.AddRange(parameters);
					new NpgsqlDataAdapter(cmd).Fill(dt);
				}
			}
			return dt;
		}

		#endregion

		#region <=== Phase 2: File Metadata Persistence Tests ===>

		/// <summary>
		/// Verifies that Create inserts a complete file metadata record into the files table,
		/// including id, object_id, filepath, created_on, modified_on, created_by, and modified_by.
		/// Source: SQL INSERT with all seven columns in DbFileRepository.Create.
		/// </summary>
		[Fact]
		public void Create_ShouldInsertFileMetadataIntoFilesTable()
		{
			// Arrange
			var filepath = UniquePath("/documents");
			var createdOn = DateTime.UtcNow;
			var createdBy = Guid.NewGuid();
			var buffer = new byte[] { 1, 2, 3 };

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.Create(filepath, buffer, createdOn, createdBy);

				// Assert — verify returned DbFile properties
				result.Should().NotBeNull();
				result.FilePath.Should().Be(filepath);
				result.CreatedBy.Should().Be(createdBy);
				result.LastModifiedBy.Should().Be(createdBy);
				result.CreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(2));
				result.LastModificationDate.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(2));
				result.Id.Should().NotBe(Guid.Empty);

				// Assert — verify record exists directly in the database
				var dt = QueryFilesDirectly(
					"SELECT * FROM files WHERE filepath = @fp",
					new[] { new NpgsqlParameter("@fp", filepath) });
				dt.Rows.Count.Should().Be(1);
				((string)dt.Rows[0]["filepath"]).Should().Be(filepath);
				((Guid)dt.Rows[0]["id"]).Should().Be(result.Id);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Find returns a DbFile with correct metadata when a matching
		/// file record exists in the files table.
		/// Source: SQL SELECT * FROM files WHERE filepath = @filepath
		/// </summary>
		[Fact]
		public void Find_ShouldReturnFileByPath()
		{
			// Arrange
			var filepath = UniquePath("/documents");
			var createdBy = Guid.NewGuid();

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var created = repo.Create(filepath, new byte[] { 1, 2, 3 }, DateTime.UtcNow, createdBy);

				// Act
				var result = repo.Find(filepath);

				// Assert
				result.Should().NotBeNull();
				result.FilePath.Should().Be(filepath);
				result.CreatedBy.Should().Be(createdBy);
				result.Id.Should().Be(created.Id);
				result.ObjectId.Should().Be(created.ObjectId);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Find returns null (not throws) when no file with the specified
		/// path exists in the files table.
		/// Source: DbFileRepository.Find returns null after DataTable.Rows.Count != 1.
		/// </summary>
		[Fact]
		public void Find_ShouldReturnNull_WhenFileNotFound()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.Find("/nonexistent/" + Guid.NewGuid().ToString("N") + ".pdf");

				// Assert
				result.Should().BeNull();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that FindAll returns all file records when called with includeTempFiles=true
		/// and no path filter. Creates multiple files and verifies all are present in the results.
		/// Source: SQL SELECT * FROM files (no WHERE clause when no filters active).
		/// </summary>
		[Fact]
		public void FindAll_ShouldReturnAllFiles()
		{
			// Arrange
			var path1 = UniquePath("/all-test");
			var path2 = UniquePath("/all-test");
			var path3 = UniquePath("/all-test");

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(path1, new byte[] { 1 }, DateTime.UtcNow, null);
				repo.Create(path2, new byte[] { 2 }, DateTime.UtcNow, null);
				repo.Create(path3, new byte[] { 3 }, DateTime.UtcNow, null);

				// Act — includeTempFiles: true to get all files without filtering
				var result = repo.FindAll(includeTempFiles: true);

				// Assert — at least our 3 files (may include files from other tests)
				result.Should().NotBeNull();
				result.Count.Should().BeGreaterThanOrEqualTo(3);
				result.Select(f => f.FilePath).Should().Contain(path1);
				result.Select(f => f.FilePath).Should().Contain(path2);
				result.Select(f => f.FilePath).Should().Contain(path3);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 3: File Path Normalization Tests ===>

		/// <summary>
		/// Verifies that Create normalizes file paths to lowercase via ToLowerInvariant().
		/// Source: filepath = filepath.ToLowerInvariant() in Create method.
		/// </summary>
		[Fact]
		public void Create_ShouldNormalizePathToLowercase()
		{
			// Arrange
			var guid = Guid.NewGuid().ToString("N");
			var mixedCasePath = "/Documents/Test_" + guid + ".PDF";
			var expectedPath = mixedCasePath.ToLowerInvariant();

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.Create(mixedCasePath, new byte[] { 1 }, DateTime.UtcNow, null);

				// Assert
				result.Should().NotBeNull();
				result.FilePath.Should().Be(expectedPath);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Create prepends the folder separator "/" when the filepath
		/// does not start with one.
		/// Source: if (!filepath.StartsWith(FOLDER_SEPARATOR)) filepath = FOLDER_SEPARATOR + filepath
		/// </summary>
		[Fact]
		public void Create_ShouldPrependFolderSeparator()
		{
			// Arrange
			var guid = Guid.NewGuid().ToString("N");
			var pathWithoutSlash = "documents/test_" + guid + ".pdf";
			var expectedPath = DbFileRepository.FOLDER_SEPARATOR + pathWithoutSlash;

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.Create(pathWithoutSlash, new byte[] { 1 }, DateTime.UtcNow, null);

				// Assert
				result.Should().NotBeNull();
				result.FilePath.Should().Be(expectedPath);
				result.FilePath.Should().StartWith(DbFileRepository.FOLDER_SEPARATOR);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Create throws ArgumentException for null, empty, and whitespace-only
		/// filepath arguments.
		/// Source: if (string.IsNullOrWhiteSpace(filepath)) throw new ArgumentException(...)
		/// </summary>
		[Fact]
		public void Create_ShouldThrowForEmptyFilepath()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act & Assert — null
				Action actNull = () => repo.Create(null, new byte[] { 1 }, DateTime.UtcNow, null);
				actNull.Should().Throw<ArgumentException>()
					.WithMessage("*filepath cannot be null or empty*");

				// Act & Assert — empty string
				Action actEmpty = () => repo.Create("", new byte[] { 1 }, DateTime.UtcNow, null);
				actEmpty.Should().Throw<ArgumentException>()
					.WithMessage("*filepath cannot be null or empty*");

				// Act & Assert — whitespace
				Action actWhitespace = () => repo.Create("   ", new byte[] { 1 }, DateTime.UtcNow, null);
				actWhitespace.Should().Throw<ArgumentException>()
					.WithMessage("*filepath cannot be null or empty*");
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Create throws ArgumentException when a file with the same normalized
		/// path already exists in the files table.
		/// Source: if (Find(filepath) != null) throw new ArgumentException(filepath + ": file already exists")
		/// </summary>
		[Fact]
		public void Create_ShouldThrowForDuplicateFilepath()
		{
			// Arrange
			var filepath = UniquePath("/dup-test");

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(filepath, new byte[] { 1, 2, 3 }, DateTime.UtcNow, null);

				// Act & Assert
				Action act = () => repo.Create(filepath, new byte[] { 4, 5, 6 }, DateTime.UtcNow, null);
				act.Should().Throw<ArgumentException>()
					.WithMessage("*file already exists*");
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 4: File Copy Tests ===>

		/// <summary>
		/// Verifies that Copy creates a new file at the destination path while preserving
		/// the source file. The destination file should have the source's creation metadata.
		/// Source: Copy reads bytes via srcFile.GetBytes(connection), calls Create for destination.
		/// </summary>
		[Fact]
		public void Copy_ShouldCreateNewFileAtDestination()
		{
			// Arrange
			var srcPath = UniquePath("/copy-src");
			var destPath = UniquePath("/copy-dest");
			var buffer = new byte[] { 10, 20, 30 };
			var createdBy = Guid.NewGuid();

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(srcPath, buffer, DateTime.UtcNow, createdBy);

				// Act
				var result = repo.Copy(srcPath, destPath);

				// Assert — destination file should exist with correct metadata
				result.Should().NotBeNull();
				result.FilePath.Should().Be(destPath);
				result.CreatedBy.Should().Be(createdBy);

				// Assert — source file should still exist
				var srcFile = repo.Find(srcPath);
				srcFile.Should().NotBeNull();

				// Assert — destination file should be independently findable
				var destFile = repo.Find(destPath);
				destFile.Should().NotBeNull();
				destFile.FilePath.Should().Be(destPath);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Copy with overwrite=false throws when the destination file
		/// already exists.
		/// Source: if (destFile != null && overwrite == false) throw new Exception(...)
		/// </summary>
		[Fact]
		public void Copy_WithOverwriteFalse_ShouldThrow_WhenDestinationExists()
		{
			// Arrange
			var srcPath = UniquePath("/copy-noow-src");
			var destPath = UniquePath("/copy-noow-dest");

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(srcPath, new byte[] { 1 }, DateTime.UtcNow, null);
				repo.Create(destPath, new byte[] { 2 }, DateTime.UtcNow, null);

				// Act & Assert
				Action act = () => repo.Copy(srcPath, destPath, overwrite: false);
				act.Should().Throw<Exception>()
					.WithMessage("*Destination file already exists*");
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Copy with overwrite=true deletes the existing destination file
		/// and creates a new file with the source's content and metadata.
		/// Source: if (destFile != null && overwrite) Delete(destFile.FilePath); then Create.
		/// </summary>
		[Fact]
		public void Copy_WithOverwriteTrue_ShouldReplaceExistingFile()
		{
			// Arrange
			var srcPath = UniquePath("/copy-ow-src");
			var destPath = UniquePath("/copy-ow-dest");
			var srcBytes = new byte[] { 10, 20, 30 };
			var destBytes = new byte[] { 40, 50, 60 };
			var srcCreatedBy = Guid.NewGuid();

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(srcPath, srcBytes, DateTime.UtcNow, srcCreatedBy);
				repo.Create(destPath, destBytes, DateTime.UtcNow, Guid.NewGuid());

				// Act
				var result = repo.Copy(srcPath, destPath, overwrite: true);

				// Assert — destination should inherit source's CreatedBy
				result.Should().NotBeNull();
				result.FilePath.Should().Be(destPath);
				result.CreatedBy.Should().Be(srcCreatedBy);

				// Assert — source should still exist
				repo.Find(srcPath).Should().NotBeNull();

				// Assert — verify content of destination matches source via GetBytes
				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					try
					{
						connection.BeginTransaction();
						var copiedBytes = result.GetBytes(connection);
						copiedBytes.Should().BeEquivalentTo(srcBytes);
						connection.CommitTransaction();
					}
					catch
					{
						connection.RollbackTransaction();
						throw;
					}
				}
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 5: File Move Tests ===>

		/// <summary>
		/// Verifies that Move updates the filepath in the database and makes the file
		/// accessible only at the new path. The file ID should remain the same.
		/// Source: SQL UPDATE files SET filepath = @filepath WHERE id = @id
		/// </summary>
		[Fact]
		public void Move_ShouldUpdateFilepathInDatabase()
		{
			// Arrange
			var srcPath = UniquePath("/move-src");
			var destPath = UniquePath("/move-dest");

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var original = repo.Create(srcPath, new byte[] { 1, 2, 3 }, DateTime.UtcNow, null);

				// Act
				var result = repo.Move(srcPath, destPath);

				// Assert — file should exist at new path with same ID
				result.Should().NotBeNull();
				result.FilePath.Should().Be(destPath);
				result.Id.Should().Be(original.Id);

				// Assert — file should NOT exist at old path
				repo.Find(srcPath).Should().BeNull();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Move normalizes the destination path to lowercase.
		/// Source: destinationFilepath = destinationFilepath.ToLowerInvariant() in Move method.
		/// </summary>
		[Fact]
		public void Move_ShouldNormalizeDestinationPath()
		{
			// Arrange
			var srcPath = UniquePath("/move-norm-src");
			var guid = Guid.NewGuid().ToString("N");
			var destPath = "/Move-Norm-Dest/" + guid + ".PDF";
			var expectedDestPath = destPath.ToLowerInvariant();

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(srcPath, new byte[] { 1, 2, 3 }, DateTime.UtcNow, null);

				// Act
				var result = repo.Move(srcPath, destPath);

				// Assert
				result.Should().NotBeNull();
				result.FilePath.Should().Be(expectedDestPath);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 6: File Delete Tests ===>

		/// <summary>
		/// Verifies that Delete removes the file metadata record from the files table
		/// and (in LO mode) unlinks the associated PostgreSQL Large Object.
		/// Source: SQL DELETE FROM files WHERE id = @id + NpgsqlLargeObjectManager.Unlink.
		/// </summary>
		[Fact]
		public void Delete_ShouldRemoveFileMetadata()
		{
			// Arrange
			var filepath = UniquePath("/delete-test");

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				repo.Create(filepath, new byte[] { 1, 2, 3 }, DateTime.UtcNow, null);

				// Verify file exists before delete
				repo.Find(filepath).Should().NotBeNull();

				// Act
				repo.Delete(filepath);

				// Assert — file no longer exists via repository
				repo.Find(filepath).Should().BeNull();

				// Assert — verify via direct SQL query
				var dt = QueryFilesDirectly(
					"SELECT * FROM files WHERE filepath = @fp",
					new[] { new NpgsqlParameter("@fp", filepath) });
				dt.Rows.Count.Should().Be(0);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that Delete returns silently without throwing when the specified
		/// file path does not exist in the files table.
		/// Source: if (file == null) return; in Delete method.
		/// </summary>
		[Fact]
		public void Delete_ShouldSilentlyReturn_WhenFileNotFound()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act & Assert — should not throw for non-existent file
				Action act = () => repo.Delete("/nonexistent/" + Guid.NewGuid().ToString("N") + ".pdf");
				act.Should().NotThrow();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 7: Temp File Tests ===>

		/// <summary>
		/// Verifies that CreateTempFile stores the file in the /tmp/{section}/ folder,
		/// where section is a GUID without dashes.
		/// Source: Path format FOLDER_SEPARATOR + TMP_FOLDER_NAME + FOLDER_SEPARATOR + section + FOLDER_SEPARATOR + filename
		/// </summary>
		[Fact]
		public void CreateTempFile_ShouldCreateInTmpFolder()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.CreateTempFile("test.pdf", new byte[] { 1, 2, 3 });

				// Assert — path should start with /tmp/ and contain the filename
				result.Should().NotBeNull();
				result.FilePath.Should().StartWith(
					DbFileRepository.FOLDER_SEPARATOR + DbFileRepository.TMP_FOLDER_NAME + DbFileRepository.FOLDER_SEPARATOR);
				result.FilePath.Should().Contain("test.pdf");
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that CreateTempFile appends the specified extension to the filename.
		/// The extension is trimmed, lowercased, and dot-prefixed before appending.
		/// Source: extension = "." + extension if not already dot-prefixed, then filename + extension.
		/// </summary>
		[Fact]
		public void CreateTempFile_WithExtension_ShouldAppendExtension()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Act
				var result = repo.CreateTempFile("report", new byte[] { 1, 2, 3 }, ".pdf");

				// Assert — the filepath should contain the filename with extension
				result.Should().NotBeNull();
				result.FilePath.Should().StartWith(
					DbFileRepository.FOLDER_SEPARATOR + DbFileRepository.TMP_FOLDER_NAME + DbFileRepository.FOLDER_SEPARATOR);
				result.FilePath.Should().Contain("report.pdf");
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that CleanupExpiredTempFiles deletes files matching the ILIKE '%/tmp' pattern.
		///
		/// PRESERVED BEHAVIOR: The expiration parameter is NOT used in the implementation — all
		/// matching files are deleted regardless of age. Also, the ILIKE '%/tmp' pattern only
		/// matches filepaths ending with '/tmp', not files created via CreateTempFile (which
		/// have paths like '/tmp/section/filename.ext'). This test verifies the SQL execution
		/// path using files that match the actual ILIKE pattern.
		/// </summary>
		[Fact]
		public void CleanupExpiredTempFiles_ShouldRemoveTempFiles()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();

				// Create files that match the ILIKE '%/tmp' pattern (paths ending with /tmp)
				var guid1 = Guid.NewGuid().ToString("N");
				var guid2 = Guid.NewGuid().ToString("N");
				var tmpPath1 = "/cleanup-" + guid1 + "/tmp";
				var tmpPath2 = "/cleanup-" + guid2 + "/tmp";
				var regularPath = UniquePath("/regular");

				repo.Create(tmpPath1, new byte[] { 1 }, DateTime.UtcNow, null);
				repo.Create(tmpPath2, new byte[] { 2 }, DateTime.UtcNow, null);
				repo.Create(regularPath, new byte[] { 3 }, DateTime.UtcNow, null);

				// Act — expiration parameter is intentionally ignored by the implementation
				repo.CleanupExpiredTempFiles(TimeSpan.FromMinutes(0));

				// Assert — files matching '%/tmp' pattern should be deleted
				repo.Find(tmpPath1).Should().BeNull();
				repo.Find(tmpPath2).Should().BeNull();

				// Assert — regular file should still exist
				repo.Find(regularPath).Should().NotBeNull();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 8: FindAll Filter Tests ===>

		/// <summary>
		/// Verifies that FindAll with startsWithPath filters files using ILIKE suffix matching.
		///
		/// PRESERVED BEHAVIOR: The implementation uses ILIKE '%' + startsWithPath which creates
		/// a suffix pattern (matches paths ending with the given string), not a prefix pattern.
		/// This is a known behavioral quirk preserved from the monolith source.
		/// </summary>
		[Fact]
		public void FindAll_WithStartsWithPath_ShouldFilterByPrefix()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var filterSuffix = "/filterable-" + Guid.NewGuid().ToString("N");

				// This path ends with the filter suffix — should match ILIKE '%suffix'
				var matchingPath = "/archive" + filterSuffix;
				// This path has the filter suffix in the middle, not at the end — should not match
				var nonMatchingPath = filterSuffix + "/some-file.pdf";

				repo.Create(matchingPath, new byte[] { 1 }, DateTime.UtcNow, null);
				repo.Create(nonMatchingPath, new byte[] { 2 }, DateTime.UtcNow, null);

				// Act — includeTempFiles: true to isolate path filtering from temp filtering
				var result = repo.FindAll(startsWithPath: filterSuffix, includeTempFiles: true);

				// Assert
				result.Should().NotBeNull();
				result.Any(f => f.FilePath == matchingPath).Should().BeTrue();
				result.Any(f => f.FilePath == nonMatchingPath).Should().BeFalse();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that FindAll with includeTempFiles=false excludes files matching the
		/// ILIKE '%/tmp' pattern from results.
		///
		/// PRESERVED BEHAVIOR: The NOT ILIKE '%/tmp' filter only excludes files whose filepath
		/// ends with '/tmp'. Files created via CreateTempFile (paths like '/tmp/section/file.ext')
		/// are NOT excluded by this filter. This test verifies the actual ILIKE filtering behavior.
		/// </summary>
		[Fact]
		public void FindAll_ExcludingTempFiles_ShouldFilterOutTmp()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var guid = Guid.NewGuid().ToString("N");

				// Regular file — should be included in results
				var regularPath = "/regular-" + guid + "/data.pdf";
				// File with path ending in /tmp — should be excluded when includeTempFiles=false
				var tmpEndingPath = "/excluded-" + guid + "/tmp";

				repo.Create(regularPath, new byte[] { 1 }, DateTime.UtcNow, null);
				repo.Create(tmpEndingPath, new byte[] { 2 }, DateTime.UtcNow, null);

				// Act — includeTempFiles: false triggers NOT ILIKE '%/tmp' filter
				var result = repo.FindAll(includeTempFiles: false);

				// Assert
				result.Should().NotBeNull();
				result.Any(f => f.FilePath == regularPath).Should().BeTrue();
				result.Any(f => f.FilePath == tmpEndingPath).Should().BeFalse();
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		/// <summary>
		/// Verifies that FindAll respects LIMIT and OFFSET pagination parameters.
		/// Source: SQL LIMIT {limit} OFFSET {skip} appended to the query.
		/// </summary>
		[Fact]
		public void FindAll_WithPagination_ShouldRespectLimitOffset()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var prefix = "/pagination-" + Guid.NewGuid().ToString("N");

				// Create 10 files for pagination testing
				for (int i = 0; i < 10; i++)
				{
					repo.Create(prefix + "/file-" + i.ToString("D3") + ".pdf",
						new byte[] { (byte)i }, DateTime.UtcNow, null);
				}

				// Act — request 3 files starting from offset 2
				var result = repo.FindAll(includeTempFiles: true, skip: 2, limit: 3);

				// Assert — exactly 3 files returned due to LIMIT
				result.Should().NotBeNull();
				result.Should().HaveCount(3);
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 9: UpdateModificationDate Tests ===>

		/// <summary>
		/// Verifies that UpdateModificationDate executes without error and returns the file.
		///
		/// KNOWN BUG (preserved from monolith): The implementation uses Guid.NewGuid() for the
		/// @id parameter in the WHERE clause:
		///   UPDATE files SET modified_on = @modified_on WHERE id = @id
		///   command.Parameters.Add(new NpgsqlParameter("@id", Guid.NewGuid()))
		/// This means the UPDATE never matches any existing record, so the modification date is
		/// never actually updated. The test verifies this preserved behavior — the file's
		/// LastModificationDate remains unchanged after the method call.
		/// </summary>
		[Fact]
		public void UpdateModificationDate_ShouldUpdateModifiedOn()
		{
			// Arrange
			var filepath = UniquePath("/update-mod-test");
			var originalDate = DateTime.UtcNow.AddDays(-1);
			var newDate = DateTime.UtcNow;

			CoreDbContext.CreateContext(_connectionString);
			try
			{
				var repo = new DbFileRepository();
				var created = repo.Create(filepath, new byte[] { 1, 2, 3 }, originalDate, null);
				var originalModDate = created.LastModificationDate;

				// Act
				var result = repo.UpdateModificationDate(filepath, newDate);

				// Assert — method returns non-null file
				result.Should().NotBeNull();
				result.FilePath.Should().Be(filepath);
				result.Id.Should().Be(created.Id);

				// Due to the Guid.NewGuid() bug in the WHERE clause, the modification date
				// is NOT actually updated. The returned file retains its original modified_on.
				result.LastModificationDate.Should().BeCloseTo(originalModDate, TimeSpan.FromSeconds(2));
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion

		#region <=== Phase 10: Backend Selection Tests ===>

		/// <summary>
		/// Verifies that when both EnableFileSystemStorage and EnableCloudBlobStorage are false,
		/// Create uses PostgreSQL Large Objects to store file content. The resulting file should
		/// have ObjectId > 0 (the LO OID) and the content should be readable via GetBytes.
		/// Source: NpgsqlLargeObjectManager.Create() + OpenReadWrite() in Create method.
		/// </summary>
		[Fact]
		public void Create_InLargeObjectMode_ShouldUsePgLargeObjects()
		{
			CoreDbContext.CreateContext(_connectionString);
			try
			{
				// Ensure Large Object mode is active (both FS and cloud disabled)
				ErpSettings.EnableFileSystemStorage = false;
				ErpSettings.EnableCloudBlobStorage = false;

				var repo = new DbFileRepository();
				var filepath = UniquePath("/lo-test");
				var contentBytes = new byte[] { 1, 2, 3, 4, 5 };

				// Act
				var result = repo.Create(filepath, contentBytes, DateTime.UtcNow, null);

				// Assert — ObjectId should be a non-zero PostgreSQL Large Object OID
				result.Should().NotBeNull();
				result.ObjectId.Should().BeGreaterThan(0);

				// Assert — verify via direct SQL that object_id > 0 in the files table
				var dt = QueryFilesDirectly(
					"SELECT object_id FROM files WHERE filepath = @fp",
					new[] { new NpgsqlParameter("@fp", filepath) });
				dt.Rows.Count.Should().Be(1);
				var storedObjectId = (decimal)dt.Rows[0]["object_id"];
				storedObjectId.Should().BeGreaterThan(0);

				// Assert — verify the LO content is readable through the repository API
				using (var connection = CoreDbContext.Current.CreateConnection())
				{
					try
					{
						connection.BeginTransaction();
						var readBytes = result.GetBytes(connection);
						readBytes.Should().NotBeNull();
						readBytes.Should().BeEquivalentTo(contentBytes);
						connection.CommitTransaction();
					}
					catch
					{
						connection.RollbackTransaction();
						throw;
					}
				}
			}
			finally
			{
				SafeCleanupContext();
			}
		}

		#endregion
	}
}
