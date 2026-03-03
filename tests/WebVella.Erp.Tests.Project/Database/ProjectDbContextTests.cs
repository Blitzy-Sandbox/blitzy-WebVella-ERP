using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Project.Database;
using TaskEntity = WebVella.Erp.Service.Project.Domain.Entities.TaskEntity;
using Xunit;

namespace WebVella.Erp.Tests.Project.Database
{
    /// <summary>
    /// Comprehensive tests for the Project service's EF Core <see cref="ProjectDbContext"/>.
    /// Validates context creation/configuration, connection to an isolated PostgreSQL
    /// container (Testcontainers.PostgreSql 4.10.0), accessibility of all project-related
    /// entity DbSets, entity-to-table mapping correctness, join table configuration,
    /// primary key configuration, cross-service reference isolation, database-per-service
    /// boundary enforcement, and CRUD round-trip operations.
    ///
    /// This is the most foundational test file in the Database test folder —
    /// all other database tests depend on the patterns established here.
    /// </summary>
    [Collection("Database")]
    public class ProjectDbContextTests : IAsyncLifetime
    {
        /// <summary>
        /// Testcontainers PostgreSQL 16 container — provides an isolated, ephemeral
        /// PostgreSQL instance per test class run. Matches production image
        /// (postgres:16-alpine) per AAP docker-compose configuration.
        /// </summary>
        private readonly PostgreSqlContainer _postgres;

        /// <summary>
        /// Connection string dynamically assigned by the Testcontainer runtime.
        /// Used by <see cref="CreateDbContext"/> to configure ProjectDbContext.
        /// </summary>
        private string _connectionString = string.Empty;

        /// <summary>
        /// Initializes the test class by building a PostgreSQL 16 container
        /// configuration. The container is NOT started here — that happens
        /// in <see cref="InitializeAsync"/>.
        /// </summary>
        public ProjectDbContextTests()
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine")
                .Build();
        }

        /// <summary>
        /// Starts the PostgreSQL container, retrieves the connection string,
        /// and applies the EF Core schema so all test methods operate against
        /// a fully provisioned database with all rec_* and rel_* tables.
        /// </summary>
        public async Task InitializeAsync()
        {
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();

            // Apply schema to the fresh PostgreSQL container database.
            // Use EnsureCreated which builds tables directly from OnModelCreating
            // model configuration — reliable for test isolation without requiring
            // migration file compilation in the test assembly context.
            using var context = CreateDbContext();
            await context.Database.EnsureCreatedAsync();
        }

        /// <summary>
        /// Disposes the PostgreSQL container, releasing the Docker resource.
        /// </summary>
        public async Task DisposeAsync()
        {
            await _postgres.DisposeAsync();
        }

        /// <summary>
        /// Centralized helper that creates a new <see cref="ProjectDbContext"/>
        /// instance configured to connect to the Testcontainer PostgreSQL database.
        /// Each call returns a fresh DbContext — callers are responsible for disposal.
        /// </summary>
        private ProjectDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(_connectionString)
                .Options;
            return new ProjectDbContext(options);
        }

        // =====================================================================
        // Phase 2: DbContext Creation and Configuration Tests
        // Per AAP requirement: "Test DbContext creation and configuration"
        // =====================================================================

        /// <summary>
        /// Verifies that CreateDbContext helper produces a non-null ProjectDbContext
        /// instance of the correct type. This is the baseline smoke test — if this
        /// fails, every other test in the class will also fail.
        /// </summary>
        [Fact]
        public void CreateDbContext_ShouldReturnNonNullInstance()
        {
            // Arrange & Act
            using var context = CreateDbContext();

            // Assert
            context.Should().NotBeNull();
            context.Should().BeOfType<ProjectDbContext>();
        }

        /// <summary>
        /// Verifies that ProjectDbContext implements IDisposable and that
        /// disposing completes without throwing an exception.
        /// </summary>
        [Fact]
        public void DbContext_ShouldBeDisposable()
        {
            // Arrange & Act — the using block exercises IDisposable.Dispose()
            var exception = Record.Exception(() =>
            {
                using var context = CreateDbContext();
                // Access the Database property to ensure the context is initialized
                var providerName = context.Database.ProviderName;
            });

            // Assert
            exception.Should().BeNull("disposing a ProjectDbContext should not throw");
        }

        /// <summary>
        /// Verifies the EF Core provider is the Npgsql PostgreSQL provider,
        /// confirming that UseNpgsql() was correctly applied to the options.
        /// </summary>
        [Fact]
        public void DbContext_ShouldHaveCorrectDatabaseProvider()
        {
            // Arrange
            using var context = CreateDbContext();

            // Act
            var providerName = context.Database.ProviderName;

            // Assert
            providerName.Should().Be("Npgsql.EntityFrameworkCore.PostgreSQL");
        }

        /// <summary>
        /// Verifies that ProjectDbContext accepts DbContextOptions through its
        /// DI constructor and can subsequently perform a basic database operation,
        /// confirming the options were applied correctly.
        /// </summary>
        [Fact]
        public void DbContext_ShouldAcceptDbContextOptions()
        {
            // Arrange
            var optionsBuilder = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(_connectionString);

            // Act
            using var context = new ProjectDbContext(optionsBuilder.Options);
            var canConnect = context.Database.CanConnect();

            // Assert
            canConnect.Should().BeTrue("the context should connect using provided options");
        }

        // =====================================================================
        // Phase 3: Connection Tests
        // Per AAP: "Test connection to isolated PostgreSQL container"
        // =====================================================================

        /// <summary>
        /// Verifies synchronous connection to the PostgreSQL Testcontainer
        /// via EF Core's CanConnect() database facade method.
        /// </summary>
        [Fact]
        public void DbContext_ShouldConnectToPostgreSqlContainer()
        {
            // Arrange
            using var context = CreateDbContext();

            // Act
            var canConnect = context.Database.CanConnect();

            // Assert
            canConnect.Should().BeTrue("ProjectDbContext should connect to the PostgreSQL container");
        }

        /// <summary>
        /// Verifies asynchronous connection to the PostgreSQL Testcontainer
        /// via EF Core's CanConnectAsync() database facade method.
        /// </summary>
        [Fact]
        public async Task DbContext_ShouldConnectAsync()
        {
            // Arrange
            using var context = CreateDbContext();

            // Act
            var canConnect = await context.Database.CanConnectAsync();

            // Assert
            canConnect.Should().BeTrue("async connection to PostgreSQL container should succeed");
        }

        /// <summary>
        /// Validates the underlying PostgreSQL connection independent of EF Core
        /// by opening a raw NpgsqlConnection and executing SELECT 1.
        /// This confirms the Testcontainer is healthy at the ADO.NET driver level.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExecuteRawSqlQuery()
        {
            // Arrange
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            // Act
            using var cmd = new NpgsqlCommand("SELECT 1", connection);
            var result = cmd.ExecuteScalar();

            // Assert
            result.Should().NotBeNull();
            result.Should().Be(1);
        }

        // =====================================================================
        // Phase 4: Entity Table Accessibility Tests
        // Per AAP: "Test that all project-related entity tables are accessible"
        // Covers all 8 record entity DbSets
        // =====================================================================

        /// <summary>
        /// Verifies the Tasks DbSet (rec_task) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeTasksDbSet()
        {
            using var context = CreateDbContext();

            context.Tasks.Should().NotBeNull();
            var tasks = context.Tasks.ToList();
            tasks.Should().NotBeNull();
            tasks.Should().BeEmpty("no tasks have been inserted yet");
        }

        /// <summary>
        /// Verifies the Timelogs DbSet (rec_timelog) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeTimelogsDbSet()
        {
            using var context = CreateDbContext();

            context.Timelogs.Should().NotBeNull();
            var timelogs = context.Timelogs.ToList();
            timelogs.Should().NotBeNull();
            timelogs.Should().BeEmpty("no timelogs have been inserted yet");
        }

        /// <summary>
        /// Verifies the Comments DbSet (rec_comment) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeCommentsDbSet()
        {
            using var context = CreateDbContext();

            context.Comments.Should().NotBeNull();
            var comments = context.Comments.ToList();
            comments.Should().NotBeNull();
            comments.Should().BeEmpty("no comments have been inserted yet");
        }

        /// <summary>
        /// Verifies the FeedItems DbSet (rec_feed_item) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeFeedItemsDbSet()
        {
            using var context = CreateDbContext();

            context.FeedItems.Should().NotBeNull();
            var feedItems = context.FeedItems.ToList();
            feedItems.Should().NotBeNull();
            feedItems.Should().BeEmpty("no feed items have been inserted yet");
        }

        /// <summary>
        /// Verifies the TaskTypes DbSet (rec_task_type) is accessible and queryable.
        /// Note: Seed data may be present from OnModelCreating HasData configuration.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeTaskTypesDbSet()
        {
            using var context = CreateDbContext();

            context.TaskTypes.Should().NotBeNull();
            var taskTypes = context.TaskTypes.ToList();
            taskTypes.Should().NotBeNull();
            // Seed data from OnModelCreating HasData — 8 task types are seeded
            taskTypes.Should().NotBeEmpty("task types are seeded from monolith patch data");
        }

        /// <summary>
        /// Verifies the TaskStatuses DbSet (rec_task_status) is accessible and queryable.
        /// Note: Seed data may be present from OnModelCreating HasData configuration.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeTaskStatusesDbSet()
        {
            using var context = CreateDbContext();

            context.TaskStatuses.Should().NotBeNull();
            var taskStatuses = context.TaskStatuses.ToList();
            taskStatuses.Should().NotBeNull();
            // Seed data from OnModelCreating HasData — 5 task statuses are seeded
            taskStatuses.Should().NotBeEmpty("task statuses are seeded from monolith patch data");
        }

        /// <summary>
        /// Verifies the Projects DbSet (rec_project) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeProjectsDbSet()
        {
            using var context = CreateDbContext();

            context.Projects.Should().NotBeNull();
            var projects = context.Projects.ToList();
            projects.Should().NotBeNull();
            projects.Should().BeEmpty("no projects have been inserted yet");
        }

        /// <summary>
        /// Verifies the Milestones DbSet (rec_milestone) is accessible and queryable.
        /// </summary>
        [Fact]
        public void DbContext_ShouldExposeMilestonesDbSet()
        {
            using var context = CreateDbContext();

            context.Milestones.Should().NotBeNull();
            var milestones = context.Milestones.ToList();
            milestones.Should().NotBeNull();
            milestones.Should().BeEmpty("no milestones have been inserted yet");
        }

        /// <summary>
        /// Programmatically iterates over all 8 record-entity DbSet properties and
        /// the 4 join-entity DbSet properties, asserting that each is non-null and
        /// queryable without throwing an exception.
        /// </summary>
        [Fact]
        public void AllDbSets_ShouldBeAccessibleWithoutException()
        {
            using var context = CreateDbContext();

            // Record entity DbSets
            var exception = Record.Exception(() =>
            {
                context.Tasks.Should().NotBeNull();
                context.Timelogs.Should().NotBeNull();
                context.Comments.Should().NotBeNull();
                context.FeedItems.Should().NotBeNull();
                context.Projects.Should().NotBeNull();
                context.TaskTypes.Should().NotBeNull();
                context.TaskStatuses.Should().NotBeNull();
                context.Milestones.Should().NotBeNull();

                // Join entity DbSets
                context.ProjectTaskRelations.Should().NotBeNull();
                context.MilestoneTaskRelations.Should().NotBeNull();
                context.ProjectMilestoneRelations.Should().NotBeNull();
                context.CommentAttachmentRelations.Should().NotBeNull();

                // Verify each DbSet is queryable (generates SQL without error)
                _ = context.Tasks.Count();
                _ = context.Timelogs.Count();
                _ = context.Comments.Count();
                _ = context.FeedItems.Count();
                _ = context.Projects.Count();
                _ = context.TaskTypes.Count();
                _ = context.TaskStatuses.Count();
                _ = context.Milestones.Count();
                _ = context.ProjectTaskRelations.Count();
                _ = context.MilestoneTaskRelations.Count();
                _ = context.ProjectMilestoneRelations.Count();
                _ = context.CommentAttachmentRelations.Count();
            });

            exception.Should().BeNull("all DbSet properties should be accessible and queryable");
        }

        // =====================================================================
        // Phase 5: Entity-to-Table Mapping Correctness Tests
        // Per AAP: "Test entity-to-table mapping correctness"
        // Validates that EF Core model maps each entity to the correct rec_* table
        // =====================================================================

        /// <summary>
        /// Verifies TaskEntity maps to the monolith's rec_task table name.
        /// </summary>
        [Fact]
        public void TaskEntity_ShouldMapToRecTaskTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(TaskEntity));

            entityType.Should().NotBeNull("TaskEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_task");
        }

        /// <summary>
        /// Verifies TimelogEntity maps to the monolith's rec_timelog table name.
        /// </summary>
        [Fact]
        public void TimelogEntity_ShouldMapToRecTimelogTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(TimelogEntity));

            entityType.Should().NotBeNull("TimelogEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_timelog");
        }

        /// <summary>
        /// Verifies CommentEntity maps to the monolith's rec_comment table name.
        /// </summary>
        [Fact]
        public void CommentEntity_ShouldMapToRecCommentTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(CommentEntity));

            entityType.Should().NotBeNull("CommentEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_comment");
        }

        /// <summary>
        /// Verifies FeedItemEntity maps to the monolith's rec_feed_item table name.
        /// </summary>
        [Fact]
        public void FeedItemEntity_ShouldMapToRecFeedItemTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(FeedItemEntity));

            entityType.Should().NotBeNull("FeedItemEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_feed_item");
        }

        /// <summary>
        /// Verifies ProjectEntity maps to the monolith's rec_project table name.
        /// </summary>
        [Fact]
        public void ProjectEntity_ShouldMapToRecProjectTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(ProjectEntity));

            entityType.Should().NotBeNull("ProjectEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_project");
        }

        /// <summary>
        /// Verifies TaskTypeEntity maps to the monolith's rec_task_type table name.
        /// </summary>
        [Fact]
        public void TaskTypeEntity_ShouldMapToRecTaskTypeTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(TaskTypeEntity));

            entityType.Should().NotBeNull("TaskTypeEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_task_type");
        }

        /// <summary>
        /// Verifies TaskStatusEntity maps to the monolith's rec_task_status table name.
        /// </summary>
        [Fact]
        public void TaskStatusEntity_ShouldMapToRecTaskStatusTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(TaskStatusEntity));

            entityType.Should().NotBeNull("TaskStatusEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_task_status");
        }

        /// <summary>
        /// Verifies MilestoneEntity maps to the monolith's rec_milestone table name.
        /// </summary>
        [Fact]
        public void MilestoneEntity_ShouldMapToRecMilestoneTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(MilestoneEntity));

            entityType.Should().NotBeNull("MilestoneEntity must be registered in the model");
            entityType.GetTableName().Should().Be("rec_milestone");
        }

        /// <summary>
        /// Validates that ALL entity types in the model follow the monolith's naming
        /// convention: record entity tables start with "rec_" and join tables start
        /// with "rel_". This ensures EQL engine compatibility per AAP 0.7.3.
        /// </summary>
        [Fact]
        public void AllEntityTableNames_ShouldFollowRecConvention()
        {
            using var context = CreateDbContext();
            var entityTypes = context.Model.GetEntityTypes().ToList();

            entityTypes.Should().NotBeEmpty("the model must contain entity types");

            foreach (var entityType in entityTypes)
            {
                var tableName = entityType.GetTableName();
                tableName.Should().NotBeNullOrEmpty(
                    $"entity type {entityType.ClrType.Name} should have a table name");

                var startsWithRec = tableName.StartsWith("rec_", StringComparison.Ordinal);
                var startsWithRel = tableName.StartsWith("rel_", StringComparison.Ordinal);

                (startsWithRec || startsWithRel).Should().BeTrue(
                    $"table '{tableName}' for entity {entityType.ClrType.Name} " +
                    "should start with 'rec_' (record) or 'rel_' (relation)");
            }
        }

        // =====================================================================
        // Phase 6: Join Table Mapping Tests
        // Validates M:N relation join entity table mappings
        // =====================================================================

        /// <summary>
        /// Verifies ProjectTaskRelation maps to rel_project_nn_task with
        /// the expected origin_id and target_id columns.
        /// </summary>
        [Fact]
        public void ProjectTaskJoin_ShouldMapToRelProjectNnTaskTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(ProjectTaskRelation));

            entityType.Should().NotBeNull("ProjectTaskRelation must be registered in the model");
            entityType.GetTableName().Should().Be("rel_project_nn_task");

            // Verify origin_id and target_id columns exist
            var properties = entityType.GetProperties().ToList();
            var columnNames = properties.Select(p => p.GetColumnName()).ToList();
            columnNames.Should().Contain("origin_id", "join table must have origin_id column");
            columnNames.Should().Contain("target_id", "join table must have target_id column");
        }

        /// <summary>
        /// Verifies MilestoneTaskRelation maps to rel_milestone_nn_task.
        /// </summary>
        [Fact]
        public void MilestoneTaskJoin_ShouldMapToRelMilestoneNnTaskTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(MilestoneTaskRelation));

            entityType.Should().NotBeNull("MilestoneTaskRelation must be registered in the model");
            entityType.GetTableName().Should().Be("rel_milestone_nn_task");

            var columnNames = entityType.GetProperties()
                .Select(p => p.GetColumnName()).ToList();
            columnNames.Should().Contain("origin_id");
            columnNames.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies ProjectMilestoneRelation maps to rel_project_nn_milestone.
        /// </summary>
        [Fact]
        public void ProjectMilestoneJoin_ShouldMapToRelProjectNnMilestoneTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(ProjectMilestoneRelation));

            entityType.Should().NotBeNull("ProjectMilestoneRelation must be registered in the model");
            entityType.GetTableName().Should().Be("rel_project_nn_milestone");

            var columnNames = entityType.GetProperties()
                .Select(p => p.GetColumnName()).ToList();
            columnNames.Should().Contain("origin_id");
            columnNames.Should().Contain("target_id");
        }

        /// <summary>
        /// Verifies CommentAttachmentRelation maps to rel_comment_nn_attachment.
        /// </summary>
        [Fact]
        public void CommentAttachmentJoin_ShouldMapToRelCommentNnAttachmentTable()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(CommentAttachmentRelation));

            entityType.Should().NotBeNull("CommentAttachmentRelation must be registered in the model");
            entityType.GetTableName().Should().Be("rel_comment_nn_attachment");

            var columnNames = entityType.GetProperties()
                .Select(p => p.GetColumnName()).ToList();
            columnNames.Should().Contain("origin_id");
            columnNames.Should().Contain("target_id");
        }

        // =====================================================================
        // Phase 7: Primary Key Configuration Tests
        // Validates PK structure for record entities and join tables
        // =====================================================================

        /// <summary>
        /// Validates that all record entities have a single-column UUID primary key
        /// named "id" of CLR type Guid, per the monolith convention of application-set
        /// GUIDs with ValueGeneratedNever.
        /// </summary>
        [Fact]
        public void AllEntityTables_ShouldHaveUuidPrimaryKey()
        {
            using var context = CreateDbContext();
            var recordEntityTypes = new[]
            {
                typeof(TaskEntity), typeof(TimelogEntity), typeof(CommentEntity),
                typeof(FeedItemEntity), typeof(ProjectEntity), typeof(TaskTypeEntity),
                typeof(TaskStatusEntity), typeof(MilestoneEntity)
            };

            foreach (var clrType in recordEntityTypes)
            {
                var entityType = context.Model.FindEntityType(clrType);
                entityType.Should().NotBeNull($"{clrType.Name} must be in the model");

                var pk = entityType.FindPrimaryKey();
                pk.Should().NotBeNull($"{clrType.Name} must have a primary key");

                pk.Properties.Should().HaveCount(1,
                    $"{clrType.Name} should have a single-column PK");

                var pkProperty = pk.Properties.Single();
                pkProperty.ClrType.Should().Be(typeof(Guid),
                    $"{clrType.Name} PK should be of type Guid");
                pkProperty.GetColumnName().Should().Be("id",
                    $"{clrType.Name} PK column should be named 'id'");
            }
        }

        /// <summary>
        /// Validates that all join tables (rel_*) have composite primary keys
        /// consisting of origin_id and target_id, both of type Guid.
        /// This matches the monolith's M:N relation table pattern.
        /// </summary>
        [Fact]
        public void JoinTables_ShouldHaveCompositePrimaryKey()
        {
            using var context = CreateDbContext();
            var joinEntityTypes = new[]
            {
                typeof(ProjectTaskRelation), typeof(MilestoneTaskRelation),
                typeof(ProjectMilestoneRelation), typeof(CommentAttachmentRelation)
            };

            foreach (var clrType in joinEntityTypes)
            {
                var entityType = context.Model.FindEntityType(clrType);
                entityType.Should().NotBeNull($"{clrType.Name} must be in the model");

                var pk = entityType.FindPrimaryKey();
                pk.Should().NotBeNull($"{clrType.Name} must have a primary key");

                pk.Properties.Should().HaveCount(2,
                    $"{clrType.Name} should have a composite PK with 2 columns");

                var columnNames = pk.Properties
                    .Select(p => p.GetColumnName())
                    .ToList();
                columnNames.Should().Contain("origin_id",
                    $"{clrType.Name} composite PK should include origin_id");
                columnNames.Should().Contain("target_id",
                    $"{clrType.Name} composite PK should include target_id");

                // Both columns should be Guid type
                pk.Properties.All(p => p.ClrType == typeof(Guid)).Should().BeTrue(
                    $"all PK columns in {clrType.Name} should be Guid");
            }
        }

        // =====================================================================
        // Phase 8: Cross-Service Reference Configuration Tests
        // Per AAP 0.7.1: Cross-service refs stored as plain UUIDs WITHOUT FK
        // =====================================================================

        /// <summary>
        /// Validates that cross-service user-referencing columns (created_by,
        /// owner_id) do NOT have foreign key constraints in the EF Core model.
        /// Per database-per-service (AAP 0.7.1), user entities are owned by the
        /// Core service — the Project service stores only the UUID reference.
        /// </summary>
        [Fact]
        public void CrossServiceUserReferences_ShouldNotHaveForeignKeys()
        {
            using var context = CreateDbContext();

            // Check each entity type with cross-service user references
            var entitiesWithUserRefs = new[]
            {
                (Type: typeof(TaskEntity), Columns: new[] { "owner_id", "created_by" }),
                (Type: typeof(TimelogEntity), Columns: new[] { "created_by" }),
                (Type: typeof(CommentEntity), Columns: new[] { "created_by" }),
                (Type: typeof(FeedItemEntity), Columns: new[] { "created_by" }),
                (Type: typeof(ProjectEntity), Columns: new[] { "owner_id" }),
            };

            foreach (var (entityClrType, crossServiceColumns) in entitiesWithUserRefs)
            {
                var entityType = context.Model.FindEntityType(entityClrType);
                entityType.Should().NotBeNull($"{entityClrType.Name} must be in the model");

                var foreignKeys = entityType.GetForeignKeys().ToList();

                foreach (var columnName in crossServiceColumns)
                {
                    // Verify no FK references a cross-service column
                    var fksOnColumn = foreignKeys
                        .Where(fk => fk.Properties.Any(p =>
                            p.GetColumnName() == columnName))
                        .ToList();

                    fksOnColumn.Should().BeEmpty(
                        $"column '{columnName}' on {entityClrType.Name} is a cross-service " +
                        "reference and must NOT have a foreign key constraint " +
                        "(AAP 0.7.1: database-per-service boundary)");
                }
            }
        }

        /// <summary>
        /// Validates that the owner_id column on TaskEntity is a nullable UUID
        /// without any foreign key constraint, confirming cross-service reference
        /// pattern per AAP 0.7.1.
        /// </summary>
        [Fact]
        public void TaskOwnerIdColumn_ShouldBeNullableUuid()
        {
            using var context = CreateDbContext();
            var entityType = context.Model.FindEntityType(typeof(TaskEntity));
            entityType.Should().NotBeNull();

            // Find the OwnerId property
            var ownerIdProperty = entityType.FindProperty("OwnerId");
            ownerIdProperty.Should().NotBeNull("TaskEntity must have an OwnerId property");

            // Verify it's nullable Guid
            ownerIdProperty.ClrType.Should().Be(typeof(Guid?),
                "OwnerId should be a nullable Guid (cross-service reference)");
            ownerIdProperty.IsNullable.Should().BeTrue(
                "OwnerId should be nullable (user may not be assigned)");

            // Verify no FK constraint on owner_id
            var foreignKeys = entityType.GetForeignKeys()
                .Where(fk => fk.Properties.Any(p => p.Name == "OwnerId"))
                .ToList();
            foreignKeys.Should().BeEmpty(
                "OwnerId must NOT have a FK constraint (cross-service to Core)");
        }

        // =====================================================================
        // Phase 9: Database-per-Service Isolation Tests
        // Per AAP 0.8.1: "each service owns its schema exclusively"
        // =====================================================================

        /// <summary>
        /// Verifies that no Core service tables (rec_user, rec_role, rec_user_file)
        /// exist in the Project service database. This validates the database-per-service
        /// boundary — Core service entities must not leak into the Project DB.
        /// </summary>
        [Fact]
        public void NoCoreServiceTables_ShouldExist()
        {
            // Query information_schema.tables for Core service table names
            var coreTables = new[] { "rec_user", "rec_role", "rec_user_file" };
            var foundTables = QueryTableNames();

            foreach (var coreTable in coreTables)
            {
                foundTables.Should().NotContain(coreTable,
                    $"Core service table '{coreTable}' must not exist in the Project database " +
                    "(AAP 0.8.1: database-per-service boundary)");
            }
        }

        /// <summary>
        /// Verifies that no CRM service tables (rec_account, rec_contact, rec_case,
        /// rec_address) exist in the Project service database.
        /// </summary>
        [Fact]
        public void NoCrmServiceTables_ShouldExist()
        {
            var crmTables = new[] { "rec_account", "rec_contact", "rec_case", "rec_address" };
            var foundTables = QueryTableNames();

            foreach (var crmTable in crmTables)
            {
                foundTables.Should().NotContain(crmTable,
                    $"CRM service table '{crmTable}' must not exist in the Project database");
            }
        }

        /// <summary>
        /// Verifies that no Mail service tables (rec_email, rec_smtp_service)
        /// exist in the Project service database.
        /// </summary>
        [Fact]
        public void NoMailServiceTables_ShouldExist()
        {
            var mailTables = new[] { "rec_email", "rec_smtp_service" };
            var foundTables = QueryTableNames();

            foreach (var mailTable in mailTables)
            {
                foundTables.Should().NotContain(mailTable,
                    $"Mail service table '{mailTable}' must not exist in the Project database");
            }
        }

        /// <summary>
        /// Verifies that ALL tables in the Project database belong exclusively to
        /// the project domain — 8 rec_* record tables plus 4 rel_* join tables,
        /// totalling exactly 12 domain tables. The __EFMigrationsHistory table
        /// (if present) is excluded from the count as an EF Core internal table.
        /// </summary>
        [Fact]
        public void OnlyProjectOwnedTables_ShouldBePresent()
        {
            var allTables = QueryTableNames();

            // Filter out EF Core internal tables
            var domainTables = allTables
                .Where(t => !t.StartsWith("__", StringComparison.Ordinal))
                .ToList();

            // Expected project-owned tables (8 record + 4 relation = 12 total)
            var expectedRecordTables = new[]
            {
                "rec_task", "rec_timelog", "rec_comment", "rec_feed_item",
                "rec_project", "rec_task_type", "rec_task_status", "rec_milestone"
            };
            var expectedRelationTables = new[]
            {
                "rel_project_nn_task", "rel_milestone_nn_task",
                "rel_project_nn_milestone", "rel_comment_nn_attachment"
            };

            foreach (var expectedTable in expectedRecordTables.Concat(expectedRelationTables))
            {
                domainTables.Should().Contain(expectedTable,
                    $"Project database should contain table '{expectedTable}'");
            }

            // All domain tables must follow naming convention
            domainTables.All(t =>
                t.StartsWith("rec_", StringComparison.Ordinal) ||
                t.StartsWith("rel_", StringComparison.Ordinal)).Should().BeTrue(
                "all domain tables should follow rec_* or rel_* naming convention");

            // Verify exact count (12 domain tables)
            domainTables.Should().HaveCount(12,
                "Project database should contain exactly 12 domain tables " +
                "(8 rec_* + 4 rel_*)");
        }

        // =====================================================================
        // Phase 10: CRUD Round-Trip Tests
        // Validates insert + read operations for key entities
        // =====================================================================

        /// <summary>
        /// Inserts a TaskEntity and reads it back, verifying all fields match.
        /// This validates the full EF Core → PostgreSQL → EF Core round-trip
        /// for the most important entity in the Project service.
        /// </summary>
        [Fact]
        public void InsertAndReadTask_ShouldSucceed()
        {
            var taskId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;

            // Fetch seeded FK references
            Guid statusId;
            Guid typeId;
            using (var seedCtx = CreateDbContext())
            {
                statusId = seedCtx.TaskStatuses.First().Id;
                typeId = seedCtx.TaskTypes.First().Id;
            }

            // Insert
            using (var context = CreateDbContext())
            {
                var task = new TaskEntity
                {
                    Id = taskId,
                    Subject = "Test Task Subject",
                    Body = "<p>Test task body</p>",
                    OwnerId = Guid.NewGuid(), // Cross-service user ref
                    CreatedOn = createdOn,
                    CreatedBy = Guid.NewGuid(), // Cross-service user ref
                    Number = 1,
                    Priority = "2",
                    StatusId = statusId,
                    TypeId = typeId,
                    XNonbillableMinutes = 0m,
                    XBillableMinutes = 0m,
                    LScope = "[\"projects\"]",
                    LRelatedRecords = "[]",
                    XSearch = "test task",
                    Key = "TST-1"
                };
                context.Tasks.Add(task);
                context.SaveChanges();
            }

            // Read back in a new context
            using (var context = CreateDbContext())
            {
                var readTask = context.Tasks.FirstOrDefault(t => t.Id == taskId);

                readTask.Should().NotBeNull("inserted task should be retrievable");
                readTask.Subject.Should().Be("Test Task Subject");
                readTask.Body.Should().Be("<p>Test task body</p>");
                readTask.Number.Should().Be(1);
                readTask.Priority.Should().Be("2");
                readTask.Key.Should().Be("TST-1");
                readTask.LScope.Should().Be("[\"projects\"]");
                readTask.XSearch.Should().Be("test task");
            }
        }

        /// <summary>
        /// Inserts a TimelogEntity and reads it back, verifying field round-trip.
        /// </summary>
        [Fact]
        public void InsertAndReadTimelog_ShouldSucceed()
        {
            var timelogId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;

            // Insert
            using (var context = CreateDbContext())
            {
                var timelog = new TimelogEntity
                {
                    Id = timelogId,
                    Body = "Worked on feature X",
                    CreatedBy = Guid.NewGuid(),
                    CreatedOn = createdOn,
                    LRelatedRecords = "[]",
                    LScope = "[\"projects\"]",
                    Minutes = 120m,
                    IsBillable = true
                };
                context.Timelogs.Add(timelog);
                context.SaveChanges();
            }

            // Read back
            using (var context = CreateDbContext())
            {
                var readTimelog = context.Timelogs.FirstOrDefault(t => t.Id == timelogId);

                readTimelog.Should().NotBeNull("inserted timelog should be retrievable");
                readTimelog.Body.Should().Be("Worked on feature X");
                readTimelog.Minutes.Should().Be(120m);
                readTimelog.IsBillable.Should().BeTrue();
                readTimelog.LScope.Should().Be("[\"projects\"]");
            }
        }

        /// <summary>
        /// Inserts a CommentEntity and reads it back, verifying field round-trip.
        /// </summary>
        [Fact]
        public void InsertAndReadComment_ShouldSucceed()
        {
            var commentId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;

            // Insert
            using (var context = CreateDbContext())
            {
                var comment = new CommentEntity
                {
                    Id = commentId,
                    Body = "This is a test comment",
                    CreatedBy = Guid.NewGuid(),
                    CreatedOn = createdOn,
                    LScope = "[\"projects\"]",
                    LRelatedRecords = "[]"
                };
                context.Comments.Add(comment);
                context.SaveChanges();
            }

            // Read back
            using (var context = CreateDbContext())
            {
                var readComment = context.Comments.FirstOrDefault(c => c.Id == commentId);

                readComment.Should().NotBeNull("inserted comment should be retrievable");
                readComment.Body.Should().Be("This is a test comment");
                readComment.LScope.Should().Be("[\"projects\"]");
            }
        }

        /// <summary>
        /// Inserts a Project, a Task, and creates a join record in
        /// rel_project_nn_task. Then verifies the relationship is queryable
        /// through the ProjectTaskRelations DbSet.
        /// </summary>
        [Fact]
        public void InsertProjectWithTasks_ShouldCreateJoinTableRecords()
        {
            var projectId = Guid.NewGuid();
            var taskId = Guid.NewGuid();

            // Fetch seeded FK references
            Guid joinStatusId;
            Guid joinTypeId;
            using (var seedCtx = CreateDbContext())
            {
                joinStatusId = seedCtx.TaskStatuses.First().Id;
                joinTypeId = seedCtx.TaskTypes.First().Id;
            }

            // Insert project, task, and join record
            using (var context = CreateDbContext())
            {
                var project = new ProjectEntity
                {
                    Id = projectId,
                    Name = "Test Project",
                    Abbr = "TST",
                    IsBillable = true
                };
                context.Projects.Add(project);

                var task = new TaskEntity
                {
                    Id = taskId,
                    Subject = "Task for project",
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    Number = 1,
                    StatusId = joinStatusId,
                    TypeId = joinTypeId,
                    Key = "TST-1",
                    XNonbillableMinutes = 0m,
                    XBillableMinutes = 0m
                };
                context.Tasks.Add(task);

                var joinRecord = new ProjectTaskRelation
                {
                    OriginId = projectId,
                    TargetId = taskId
                };
                context.ProjectTaskRelations.Add(joinRecord);

                context.SaveChanges();
            }

            // Read back the relationship
            using (var context = CreateDbContext())
            {
                var joinRecords = context.ProjectTaskRelations
                    .Where(r => r.OriginId == projectId && r.TargetId == taskId)
                    .ToList();

                joinRecords.Should().HaveCount(1,
                    "one join record should link the project to the task");
                joinRecords[0].OriginId.Should().Be(projectId);
                joinRecords[0].TargetId.Should().Be(taskId);

                // Verify project and task exist independently
                var project = context.Projects.FirstOrDefault(p => p.Id == projectId);
                project.Should().NotBeNull();
                project.Name.Should().Be("Test Project");

                var task = context.Tasks.FirstOrDefault(t => t.Id == taskId);
                task.Should().NotBeNull();
                task.Subject.Should().Be("Task for project");
            }
        }

        // =====================================================================
        // Private Helper Methods
        // =====================================================================

        /// <summary>
        /// Queries PostgreSQL information_schema.tables for all table names in the
        /// public schema. Used by database-per-service isolation tests to verify
        /// table ownership boundaries.
        /// </summary>
        /// <returns>List of table names in the public schema.</returns>
        private List<string> QueryTableNames()
        {
            var tables = new List<string>();
            using var connection = new NpgsqlConnection(_connectionString);
            connection.Open();

            using var cmd = new NpgsqlCommand(
                "SELECT table_name FROM information_schema.tables " +
                "WHERE table_schema = 'public' ORDER BY table_name",
                connection);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }
    }
}
