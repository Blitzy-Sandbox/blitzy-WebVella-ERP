using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.Inventory.DataAccess;

namespace WebVellaErp.Inventory.Services
{
    /// <summary>
    /// Defines the contract for all Inventory (Project Management) business logic operations.
    /// Consolidates operations from 7 monolith source files: TaskService, BaseService,
    /// CommentService, FeedItemService, TimeLogService, ProjectService, ReportService.
    /// All methods are async unless explicitly synchronous (ValidateTaskCreation, GetTaskIconAndColor).
    /// </summary>
    public interface ITaskService
    {
        // ── Task CRUD Operations ──
        Task<Models.Task> SetCalculationFieldsAsync(Guid taskId);
        Task<List<Models.TaskStatus>> GetTaskStatusesAsync();
        Task<Models.Task?> GetTaskAsync(Guid taskId);
        Task<List<Models.Task>> GetTaskQueueAsync(Guid? projectId, Guid? userId, Models.TasksDueType type = Models.TasksDueType.All, int? limit = null, bool includeProjectData = false);
        void GetTaskIconAndColor(string priorityValue, out string iconClass, out string color);
        Task StartTaskTimelogAsync(Guid taskId);
        Task StopTaskTimelogAsync(Guid taskId);
        Task SetStatusAsync(Guid taskId, Guid statusId);
        Task<List<Models.Task>> GetTasksThatNeedStartingAsync();
        Task<Models.Task> CreateTaskAsync(Models.Task task);

        // ── Task Lifecycle Hook Operations ──
        Task<Models.Task> PrepopulateNewTaskAsync(Guid currentUserId, Guid? projectIdFromQuery);
        void ValidateTaskCreation(Models.Task record, List<string> errors);
        Task PostCreateTaskAsync(Models.Task record, Guid currentUserId);
        Task PreUpdateTaskAsync(Models.Task record, Guid currentUserId, List<string> errors);
        Task PostUpdateTaskAsync(Models.Task record, Guid currentUserId);
        Task AddWatcherAsync(Guid taskId, Guid userId);
        Task RemoveWatcherAsync(Guid taskId, Guid userId);

        // ── Timelog Operations ──
        Task CreateTimelogAsync(Guid? id, Guid? createdBy, DateTime? createdOn, DateTime? loggedOn, int minutes, bool isBillable, string body, List<string> scope, List<Guid> relatedRecords);
        Task DeleteTimelogAsync(Guid recordId, Guid currentUserId);
        Task<List<Models.Timelog>> GetTimelogsForPeriodAsync(Guid? projectId, Guid? userId, DateTime startDate, DateTime endDate);
        Task HandleTimelogCreationHookAsync(Models.Timelog record, Guid currentUserId);
        Task HandleTimelogDeletionHookAsync(Guid recordId);
        Task HandleTrackTimePagePostAsync(Guid taskId, int minutes, DateTime loggedOn, bool isBillable, string body, Guid currentUserId);

        // ── Comment Operations ──
        Task CreateCommentAsync(Guid? id, Guid? createdBy, DateTime? createdOn, string body, Guid? parentId, List<string> scope, List<Guid> relatedRecords);
        Task DeleteCommentAsync(Guid recordId, Guid currentUserId);
        Task HandleCommentCreationPreHookAsync(string entityName, Models.Comment record, Guid currentUserId, List<string> errors);
        Task HandleCommentCreationPostHookAsync(string entityName, Models.Comment record, Guid currentUserId);

        // ── Feed Item, Project & Report Operations ──
        Task CreateFeedItemAsync(Guid? id, Guid? createdBy, DateTime? createdOn, string subject, string body, List<string> relatedRecords, List<string> scope, string type);
        Task<Models.Project> GetProjectAsync(Guid projectId);
        Task<List<Models.Timelog>> GetProjectTimelogsAsync(Guid projectId);
        Task<List<Dictionary<string, object>>> GetTimelogReportDataAsync(int year, int month, Guid? accountId);
    }

    /// <summary>
    /// Consolidated business logic service for the Inventory (Project Management) microservice.
    /// Replaces the monolith's 7 service files (TaskService, BaseService, CommentService,
    /// FeedItemService, TimeLogService, ProjectService, ReportService) with a single DI-friendly
    /// service backed by DynamoDB (via IInventoryRepository) and SNS (domain events).
    ///
    /// Key architectural changes from monolith:
    ///   - Constructor injection replaces BaseService's direct instantiation (RecordManager, EntityManager, etc.)
    ///   - SecurityContext.CurrentUser replaced by explicit currentUserId parameters (from Lambda JWT claims)
    ///   - EQL queries replaced by IInventoryRepository DynamoDB operations
    ///   - Synchronous hooks replaced by async SNS domain event publishing
    ///   - Newtonsoft.Json preserved ONLY for l_scope/l_related_records backward compatibility
    ///   - System.Text.Json used for all other serialization (SNS payloads)
    /// </summary>
    public class TaskService : ITaskService
    {
        private readonly IInventoryRepository _repository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<TaskService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _snsTopicArn;

        /// <summary>
        /// Monolith SystemIds.SystemUserId — default creator for system-generated records.
        /// Source: WebVella.Erp/Api/Definitions.cs SystemIds class.
        /// </summary>
        private static readonly Guid SystemUserId = new Guid("bdc56420-caf0-4030-8a0e-d264f6f47b04");

        /// <summary>
        /// Hard-coded "Not Started" task status ID from monolith.
        /// Source: TaskService.cs line 548 — used by GetTasksThatNeedStartingAsync.
        /// </summary>
        private static readonly Guid NotStartedStatusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");

        /// <summary>
        /// Maximum length for HTML snippet extraction used in feed item bodies.
        /// </summary>
        private const int SnippetMaxLength = 200;

        public TaskService(
            IInventoryRepository repository,
            IAmazonSimpleNotificationService snsClient,
            ILogger<TaskService> logger,
            IConfiguration configuration)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _snsTopicArn = _configuration["SNS:InventoryTopicArn"]
                ?? throw new InvalidOperationException("SNS:InventoryTopicArn configuration is required but was not found.");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TASK CRUD OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Task> SetCalculationFieldsAsync(Guid taskId)
        {
            _logger.LogInformation("Calculating fields for task {TaskId}", taskId);

            var task = await _repository.GetTaskByIdAsync(taskId);
            if (task == null)
            {
                throw new InvalidOperationException($"Task with ID {taskId} was not found.");
            }

            // Resolve project association from LRelatedRecords (replaces EQL $project_nn_task join)
            Models.Project? project = await ResolveProjectForTaskAsync(task);
            string projectAbbr = project?.Abbr ?? string.Empty;
            Guid projectId = project?.Id ?? Guid.Empty;
            Guid projectOwnerId = project?.OwnerId ?? Guid.Empty;

            // Calculate key: project abbreviation + "-" + task number (source line 76)
            task.Key = string.IsNullOrEmpty(projectAbbr)
                ? task.Number.ToString("N0")
                : projectAbbr + "-" + task.Number.ToString("N0");

            await _repository.UpdateTaskAsync(task);

            _logger.LogInformation("Calculated key {Key} for task {TaskId} in project {ProjectId}", task.Key, taskId, projectId);
            return task;
        }

        /// <inheritdoc />
        public async Task<List<Models.TaskStatus>> GetTaskStatusesAsync()
        {
            _logger.LogInformation("Retrieving all task statuses");
            var statuses = await _repository.GetAllTaskStatusesAsync();
            return statuses.OrderBy(s => s.SortOrder).ToList();
        }

        /// <inheritdoc />
        public async Task<Models.Task?> GetTaskAsync(Guid taskId)
        {
            _logger.LogInformation("Retrieving task {TaskId}", taskId);
            return await _repository.GetTaskByIdAsync(taskId);
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> GetTaskQueueAsync(
            Guid? projectId,
            Guid? userId,
            Models.TasksDueType type = Models.TasksDueType.All,
            int? limit = null,
            bool includeProjectData = false)
        {
            _logger.LogInformation(
                "Getting task queue: projectId={ProjectId}, userId={UserId}, type={DueType}, limit={Limit}",
                projectId, userId, type, limit);

            // Step 1: Fetch base task set from repository using the most selective index
            List<Models.Task> tasks;
            if (projectId.HasValue)
            {
                tasks = await _repository.GetTasksByProjectAsync(projectId.Value);
            }
            else if (userId.HasValue)
            {
                tasks = await _repository.GetTasksByOwnerAsync(userId.Value);
            }
            else
            {
                tasks = await _repository.QueryTasksAsync();
            }

            // Step 2: Exclude tasks with closed statuses (source lines 154-173)
            var allStatuses = await _repository.GetAllTaskStatusesAsync();
            var closedStatusIds = allStatuses
                .Where(s => s.IsClosed)
                .Select(s => s.Id)
                .ToHashSet();

            tasks = tasks.Where(t => !t.StatusId.HasValue || !closedStatusIds.Contains(t.StatusId.Value)).ToList();

            // Step 3: Apply owner filter if both projectId and userId are set
            if (projectId.HasValue && userId.HasValue)
            {
                tasks = tasks.Where(t => t.OwnerId == userId.Value).ToList();
            }

            // Step 4: Apply due-type date window filters in-memory (source lines 119-135)
            var currentDateStart = DateTime.UtcNow.Date;
            var currentDateEnd = DateTime.UtcNow.Date.AddDays(1);

            tasks = type switch
            {
                Models.TasksDueType.StartTimeDue => tasks
                    .Where(t => !t.StartTime.HasValue || t.StartTime.Value < currentDateEnd)
                    .ToList(),
                Models.TasksDueType.StartTimeNotDue => tasks
                    .Where(t => t.StartTime.HasValue && t.StartTime.Value > currentDateEnd)
                    .ToList(),
                Models.TasksDueType.EndTimeOverdue => tasks
                    .Where(t => t.EndTime.HasValue && t.EndTime.Value < currentDateStart)
                    .ToList(),
                Models.TasksDueType.EndTimeDueToday => tasks
                    .Where(t => t.EndTime.HasValue && t.EndTime.Value >= currentDateStart && t.EndTime.Value < currentDateEnd)
                    .ToList(),
                Models.TasksDueType.EndTimeNotDue => tasks
                    .Where(t => !t.EndTime.HasValue || t.EndTime.Value >= currentDateEnd)
                    .ToList(),
                _ => tasks // TasksDueType.All — no date filter
            };

            // Step 5: Apply sorting based on due type (source lines 182-204)
            tasks = type switch
            {
                Models.TasksDueType.EndTimeOverdue => tasks
                    .OrderBy(t => t.EndTime)
                    .ThenByDescending(t => t.Priority)
                    .ToList(),
                Models.TasksDueType.EndTimeDueToday => tasks
                    .OrderByDescending(t => t.Priority)
                    .ToList(),
                Models.TasksDueType.EndTimeNotDue => tasks
                    .OrderBy(t => t.EndTime)
                    .ThenByDescending(t => t.Priority)
                    .ToList(),
                Models.TasksDueType.StartTimeDue or Models.TasksDueType.StartTimeNotDue => tasks
                    .OrderBy(t => t.EndTime)
                    .ThenByDescending(t => t.Priority)
                    .ToList(),
                _ => tasks
            };

            // Step 6: Apply limit (source line 209: PAGE 1 PAGESIZE {limit})
            if (limit.HasValue && limit.Value > 0)
            {
                tasks = tasks.Take(limit.Value).ToList();
            }

            _logger.LogInformation("Task queue returned {Count} tasks", tasks.Count);
            return tasks;
        }

        /// <inheritdoc />
        public void GetTaskIconAndColor(string priorityValue, out string iconClass, out string color)
        {
            // Priority-to-icon/color mapping replaces monolith's EntityManager.ReadEntity("task")
            // metadata lookup (source lines 217-230). Uses cached task type metadata.
            switch (priorityValue?.ToLowerInvariant())
            {
                case "1": // Urgent
                    iconClass = "fas fa-exclamation-circle";
                    color = "#d50000";
                    break;
                case "2": // High
                    iconClass = "fas fa-arrow-circle-up";
                    color = "#ff6d00";
                    break;
                case "3": // Medium
                    iconClass = "fas fa-minus-circle";
                    color = "#ffd600";
                    break;
                case "4": // Low
                    iconClass = "fas fa-arrow-circle-down";
                    color = "#00c853";
                    break;
                default:
                    iconClass = "fas fa-minus-circle";
                    color = "#999999";
                    break;
            }

            // Ensure task types are loaded for any future label resolution
            var _ = _repository.GetAllTaskTypesAsync().GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task StartTaskTimelogAsync(Guid taskId)
        {
            _logger.LogInformation("Starting timelog for task {TaskId}", taskId);
            var task = await _repository.GetTaskByIdAsync(taskId);
            if (task == null)
            {
                throw new InvalidOperationException($"Task with ID {taskId} was not found.");
            }

            task.TimelogStartedOn = DateTime.UtcNow;
            await _repository.UpdateTaskAsync(task);
            _logger.LogInformation("Timelog started at {StartTime} for task {TaskId}", task.TimelogStartedOn, taskId);
        }

        /// <inheritdoc />
        public async Task StopTaskTimelogAsync(Guid taskId)
        {
            _logger.LogInformation("Stopping timelog for task {TaskId}", taskId);
            var task = await _repository.GetTaskByIdAsync(taskId);
            if (task == null)
            {
                throw new InvalidOperationException($"Task with ID {taskId} was not found.");
            }

            task.TimelogStartedOn = null;
            await _repository.UpdateTaskAsync(task);
            _logger.LogInformation("Timelog stopped for task {TaskId}", taskId);
        }

        /// <inheritdoc />
        public async Task SetStatusAsync(Guid taskId, Guid statusId)
        {
            _logger.LogInformation("Setting status {StatusId} on task {TaskId}", statusId, taskId);
            var task = await _repository.GetTaskByIdAsync(taskId);
            if (task == null)
            {
                throw new InvalidOperationException($"Task with ID {taskId} was not found.");
            }

            task.StatusId = statusId;

            // Set CompletedOn when transitioning to a closed status
            var allStatuses = await _repository.GetAllTaskStatusesAsync();
            var targetStatus = allStatuses.FirstOrDefault(s => s.Id == statusId);
            if (targetStatus != null && targetStatus.IsClosed && !task.CompletedOn.HasValue)
            {
                task.CompletedOn = DateTime.UtcNow;
            }

            await _repository.UpdateTaskAsync(task);
            _logger.LogInformation("Task {TaskId} status set to {StatusId} ({Label})", taskId, statusId, targetStatus?.Label ?? "unknown");
        }

        /// <inheritdoc />
        public async Task<List<Models.Task>> GetTasksThatNeedStartingAsync()
        {
            _logger.LogInformation("Finding tasks that need starting (status=NotStarted, start_time<=today)");

            // Get tasks with "Not Started" status (source line 548: hard-coded GUID)
            var notStartedTasks = await _repository.GetTasksByStatusAsync(NotStartedStatusId);

            // Filter to tasks with start_time <= end of today (source lines 549-551)
            var currentDateEnd = DateTime.UtcNow.Date.AddDays(1);
            var tasksToStart = notStartedTasks
                .Where(t => t.StartTime.HasValue && t.StartTime.Value <= currentDateEnd)
                .ToList();

            _logger.LogInformation("Found {Count} tasks that need starting", tasksToStart.Count);
            return tasksToStart;
        }

        /// <inheritdoc />
        /// <summary>
        /// Creates a new task record by delegating to the repository.
        /// This is the raw persistence operation — callers should invoke
        /// ValidateTaskCreation() before and PostCreateTaskAsync() after.
        /// </summary>
        public async Task<Models.Task> CreateTaskAsync(Models.Task task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _logger.LogInformation("Creating task {TaskId} with subject '{Subject}'", task.Id, task.Subject);

            if (task.Id == Guid.Empty)
            {
                task.Id = Guid.NewGuid();
            }
            if (task.CreatedOn == default)
            {
                task.CreatedOn = DateTime.UtcNow;
            }

            var created = await _repository.CreateTaskAsync(task);
            _logger.LogInformation("Task {TaskId} created successfully", created.Id);
            return created;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TASK LIFECYCLE HOOK OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Task> PrepopulateNewTaskAsync(Guid currentUserId, Guid? projectIdFromQuery)
        {
            _logger.LogInformation("Prepopulating new task for user {UserId}, project query {ProjectId}", currentUserId, projectIdFromQuery);

            var newTask = new Models.Task
            {
                Id = Guid.NewGuid(),
                OwnerId = currentUserId,
                CreatedBy = currentUserId,
                CreatedOn = DateTime.UtcNow,
                StartTime = DateTime.UtcNow.Date,
                EndTime = DateTime.UtcNow.Date.AddDays(1),
                XBillableMinutes = 0,
                XNonBillableMinutes = 0,
                EstimatedMinutes = 0
            };

            // Pre-select project from query parameter (source lines 259-268)
            if (projectIdFromQuery.HasValue)
            {
                var project = await _repository.GetProjectByIdAsync(projectIdFromQuery.Value);
                if (project != null)
                {
                    newTask.LRelatedRecords = JsonConvert.SerializeObject(new List<Guid> { project.Id });
                }
            }
            else
            {
                // Fall back to the last project the user worked on (source lines 270-289)
                var recentTasks = await _repository.GetTasksByOwnerAsync(currentUserId, limit: 1);
                if (recentTasks.Any())
                {
                    var lastTask = recentTasks.First();
                    var lastProject = await ResolveProjectForTaskAsync(lastTask);
                    if (lastProject != null)
                    {
                        newTask.LRelatedRecords = JsonConvert.SerializeObject(new List<Guid> { lastProject.Id });
                    }
                }
            }

            return newTask;
        }

        /// <inheritdoc />
        public void ValidateTaskCreation(Models.Task record, List<string> errors)
        {
            // Validate that exactly one project is specified (source lines 300-330)
            if (string.IsNullOrWhiteSpace(record.LRelatedRecords))
            {
                errors.Add("A project is required for task creation.");
                return;
            }

            List<Guid>? relatedRecords;
            try
            {
                relatedRecords = JsonConvert.DeserializeObject<List<Guid>>(record.LRelatedRecords);
            }
            catch
            {
                errors.Add("Invalid related records format.");
                return;
            }

            if (relatedRecords == null || relatedRecords.Count == 0)
            {
                errors.Add("A project is required for task creation.");
            }

            // Validate Subject is provided
            if (string.IsNullOrWhiteSpace(record.Subject))
            {
                errors.Add("Task subject is required.");
            }
        }

        /// <inheritdoc />
        public async Task PostCreateTaskAsync(Models.Task record, Guid currentUserId)
        {
            _logger.LogInformation("Post-create processing for task {TaskId} by user {UserId}", record.Id, currentUserId);

            // Step 1: Persist the task record via repository (source: RecordManager.CreateRecord)
            record.CreatedBy = currentUserId;
            record.CreatedOn = record.CreatedOn == default ? DateTime.UtcNow : record.CreatedOn;
            var createdTask = await _repository.CreateTaskAsync(record);

            try
            {
                // Step 2: Calculate key field (source line 338)
                var updatedTask = await SetCalculationFieldsAsync(createdTask.Id);
                string key = updatedTask.Key ?? createdTask.Id.ToString();
                string subject = updatedTask.Subject ?? string.Empty;

                // Step 3: Resolve project for watcher seeding
                Models.Project? project = await ResolveProjectForTaskAsync(updatedTask);
                Guid projectId = project?.Id ?? Guid.Empty;

                // Step 4: Seed initial watchers (source lines 343-379)
                var watcherIds = new HashSet<Guid>();
                if (updatedTask.OwnerId.HasValue && updatedTask.OwnerId.Value != Guid.Empty)
                {
                    watcherIds.Add(updatedTask.OwnerId.Value);
                }
                if (updatedTask.CreatedBy != Guid.Empty)
                {
                    watcherIds.Add(updatedTask.CreatedBy);
                }
                if (project != null && project.OwnerId != Guid.Empty)
                {
                    watcherIds.Add(project.OwnerId);
                }

                foreach (var watcherId in watcherIds)
                {
                    await _repository.AddTaskWatcherAsync(createdTask.Id, watcherId);
                    _logger.LogInformation("Added watcher {WatcherId} to task {TaskId}", watcherId, createdTask.Id);
                }

                // Step 5: Create activity feed item (source lines 382-393)
                var feedSubject = $"created <a href=\"/projects/tasks/tasks/r/{createdTask.Id}/details\">[{key}] {subject}</a>";
                var feedBody = GetSnippetFromHtml(updatedTask.Body ?? string.Empty);
                var feedRelatedRecords = new List<string> { createdTask.Id.ToString(), projectId.ToString() };
                feedRelatedRecords.AddRange(watcherIds.Select(w => w.ToString()));

                await CreateFeedItemAsync(
                    id: null,
                    createdBy: currentUserId,
                    createdOn: DateTime.UtcNow,
                    subject: feedSubject,
                    body: feedBody,
                    relatedRecords: feedRelatedRecords,
                    scope: new List<string> { "projects" },
                    type: "task");

                // Step 6: Publish SNS domain event (replaces post-hook propagation)
                await PublishDomainEventAsync("inventory.task.created", new
                {
                    taskId = createdTask.Id,
                    key,
                    subject,
                    projectId,
                    ownerId = updatedTask.OwnerId,
                    createdBy = currentUserId,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Post-create processing failed for task {TaskId}. Rolling back.", record.Id);
                await _repository.DeleteTaskAsync(createdTask.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task PreUpdateTaskAsync(Models.Task record, Guid currentUserId, List<string> errors)
        {
            _logger.LogInformation("Pre-update processing for task {TaskId} by user {UserId}", record.Id, currentUserId);

            // Load the existing task to detect changes (source line 398)
            var existingTask = await _repository.GetTaskByIdAsync(record.Id);
            if (existingTask == null)
            {
                errors.Add($"Task with ID {record.Id} was not found.");
                return;
            }

            // Detect project change (source lines 402-488)
            Models.Project? oldProject = await ResolveProjectForTaskAsync(existingTask);
            Models.Project? newProject = await ResolveProjectForTaskAsync(record);

            Guid oldProjectId = oldProject?.Id ?? Guid.Empty;
            Guid newProjectId = newProject?.Id ?? Guid.Empty;

            if (oldProjectId != newProjectId && newProjectId != Guid.Empty)
            {
                _logger.LogInformation("Project changed for task {TaskId}: {OldProjectId} -> {NewProjectId}",
                    record.Id, oldProjectId, newProjectId);

                // Remove old project-task relation via watcher cleanup (source lines 453-456)
                if (oldProject != null)
                {
                    await _repository.RemoveTaskWatcherAsync(record.Id, oldProject.OwnerId);
                }

                // Update key with new project abbreviation (source line 459)
                if (newProject != null)
                {
                    record.Key = newProject.Abbr + "-" + record.Number.ToString("N0");

                    // Add new project owner to watchers if not already present (source lines 474-485)
                    var currentWatchers = await _repository.GetTaskWatchersAsync(record.Id);
                    if (!currentWatchers.Contains(newProject.OwnerId))
                    {
                        await _repository.AddTaskWatcherAsync(record.Id, newProject.OwnerId);
                        _logger.LogInformation("Added new project owner {OwnerId} as watcher on task {TaskId}",
                            newProject.OwnerId, record.Id);
                    }
                }
            }

            record.LastModifiedBy = currentUserId;
            record.LastModifiedOn = DateTime.UtcNow;
        }

        /// <inheritdoc />
        public async Task PostUpdateTaskAsync(Models.Task record, Guid currentUserId)
        {
            _logger.LogInformation("Post-update processing for task {TaskId} by user {UserId}", record.Id, currentUserId);

            // Step 1: Recalculate key via SetCalculationFieldsAsync (source lines 498-501)
            await SetCalculationFieldsAsync(record.Id);

            // Step 2: Ensure owner is in watchers list (source lines 503-531)
            if (record.OwnerId.HasValue && record.OwnerId.Value != Guid.Empty)
            {
                var watchers = await _repository.GetTaskWatchersAsync(record.Id);
                if (!watchers.Contains(record.OwnerId.Value))
                {
                    await _repository.AddTaskWatcherAsync(record.Id, record.OwnerId.Value);
                    _logger.LogInformation("Added owner {OwnerId} as watcher on task {TaskId}", record.OwnerId, record.Id);
                }
            }

            // Step 3: Publish SNS domain event
            await PublishDomainEventAsync("inventory.task.updated", new
            {
                taskId = record.Id,
                key = record.Key,
                subject = record.Subject,
                statusId = record.StatusId,
                ownerId = record.OwnerId,
                updatedBy = currentUserId,
                timestamp = DateTime.UtcNow
            });
        }

        /// <inheritdoc />
        /// <summary>
        /// Adds a user as a watcher on a task by delegating to the repository.
        /// Replaces monolith RecordManager.CreateRelationManyToManyRecord(watchRelation.Id, userId, taskId).
        /// </summary>
        public async Task AddWatcherAsync(Guid taskId, Guid userId)
        {
            _logger.LogInformation("Adding watcher {UserId} to task {TaskId}", userId, taskId);
            await _repository.AddTaskWatcherAsync(taskId, userId);
        }

        /// <inheritdoc />
        /// <summary>
        /// Removes a user from the watcher list on a task by delegating to the repository.
        /// Replaces monolith RecordManager.RemoveRelationManyToManyRecord(watchRelation.Id, userId, taskId).
        /// </summary>
        public async Task RemoveWatcherAsync(Guid taskId, Guid userId)
        {
            _logger.LogInformation("Removing watcher {UserId} from task {TaskId}", userId, taskId);
            await _repository.RemoveTaskWatcherAsync(taskId, userId);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TIMELOG OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task CreateTimelogAsync(
            Guid? id,
            Guid? createdBy,
            DateTime? createdOn,
            DateTime? loggedOn,
            int minutes,
            bool isBillable,
            string body,
            List<string> scope,
            List<Guid> relatedRecords)
        {
            var timelogId = id ?? Guid.NewGuid();
            var effectiveCreatedBy = createdBy ?? SystemUserId;
            var effectiveCreatedOn = createdOn ?? DateTime.UtcNow;
            var effectiveLoggedOn = loggedOn?.ToUniversalTime() ?? DateTime.UtcNow;

            _logger.LogInformation("Creating timelog {TimelogId} by user {CreatedBy}, {Minutes} minutes",
                timelogId, effectiveCreatedBy, minutes);

            var timelog = new Models.Timelog
            {
                Id = timelogId,
                CreatedBy = effectiveCreatedBy,
                CreatedOn = effectiveCreatedOn,
                LoggedOn = effectiveLoggedOn,
                Minutes = minutes,
                IsBillable = isBillable,
                Body = body ?? string.Empty,
                LScope = JsonConvert.SerializeObject(scope ?? new List<string>()),
                LRelatedRecords = JsonConvert.SerializeObject(relatedRecords ?? new List<Guid>())
            };

            await _repository.CreateTimelogAsync(timelog);
            _logger.LogInformation("Timelog {TimelogId} created successfully", timelogId);
        }

        /// <inheritdoc />
        public async Task DeleteTimelogAsync(Guid recordId, Guid currentUserId)
        {
            _logger.LogInformation("Deleting timelog {TimelogId} by user {UserId}", recordId, currentUserId);

            var timelog = await _repository.GetTimelogByIdAsync(recordId);
            if (timelog == null)
            {
                throw new InvalidOperationException($"Timelog with ID {recordId} was not found.");
            }

            // Author-only deletion check (source lines 62-71)
            if (timelog.CreatedBy != currentUserId)
            {
                throw new InvalidOperationException("Only the author can delete this timelog entry.");
            }

            await _repository.DeleteTimelogAsync(recordId);
            _logger.LogInformation("Timelog {TimelogId} deleted successfully", recordId);
        }

        /// <inheritdoc />
        public async Task<List<Models.Timelog>> GetTimelogsForPeriodAsync(
            Guid? projectId,
            Guid? userId,
            DateTime startDate,
            DateTime endDate)
        {
            _logger.LogInformation("Getting timelogs for period {Start} to {End}, project={ProjectId}, user={UserId}",
                startDate, endDate, projectId, userId);

            var timelogs = await _repository.GetTimelogsByDateRangeAsync(startDate, endDate, userId, projectId);

            _logger.LogInformation("Retrieved {Count} timelogs for period", timelogs.Count);
            return timelogs;
        }

        /// <inheritdoc />
        public async Task HandleTimelogCreationHookAsync(Models.Timelog record, Guid currentUserId)
        {
            _logger.LogInformation("Handling timelog creation hook for timelog {TimelogId}", record.Id);

            // Step 1: Validate scope contains "projects" (source lines 190-191)
            List<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(record.LScope))
            {
                scope = JsonConvert.DeserializeObject<List<string>>(record.LScope);
            }
            if (scope == null || !scope.Contains("projects"))
            {
                _logger.LogInformation("Timelog {TimelogId} is not project-scoped, skipping hook", record.Id);
                return;
            }

            // Step 2: Deserialize related records to find task IDs (source line 202)
            List<Guid>? relatedRecordIds = null;
            if (!string.IsNullOrWhiteSpace(record.LRelatedRecords))
            {
                relatedRecordIds = JsonConvert.DeserializeObject<List<Guid>>(record.LRelatedRecords);
            }
            if (relatedRecordIds == null || relatedRecordIds.Count == 0)
            {
                _logger.LogWarning("Timelog {TimelogId} has no related records, skipping hook", record.Id);
                return;
            }

            // Step 3: Find the related task (source lines 203-215)
            Models.Task? relatedTask = null;
            Guid taskId = Guid.Empty;
            foreach (var relId in relatedRecordIds)
            {
                var candidate = await _repository.GetTaskByIdAsync(relId);
                if (candidate != null)
                {
                    relatedTask = candidate;
                    taskId = relId;
                    break;
                }
            }

            if (relatedTask == null)
            {
                _logger.LogWarning("No related task found for timelog {TimelogId}", record.Id);
                return;
            }

            // Step 4: Update task aggregate minutes (source lines 227-243)
            if (record.IsBillable)
            {
                relatedTask.XBillableMinutes += record.Minutes;
            }
            else
            {
                relatedTask.XNonBillableMinutes += record.Minutes;
            }

            // Clear timelog_started_on on the task (source line 240)
            relatedTask.TimelogStartedOn = null;
            await _repository.UpdateTaskAsync(relatedTask);

            // Update the timelog with any hook-derived modifications
            await _repository.UpdateTimelogAsync(record);

            // Step 5: Create feed item (source lines 248-277)
            string key = relatedTask.Key ?? relatedTask.Id.ToString();
            string subject = relatedTask.Subject ?? string.Empty;
            string loggedTypeString = record.IsBillable ? "billable" : "non-billable";
            var feedSubject = $"logged {record.Minutes} {loggedTypeString} minutes on " +
                $"<a href=\"/projects/tasks/tasks/r/{taskId}/details\">[{key}] {subject}</a>";

            var feedBody = GetSnippetFromHtml(record.Body ?? string.Empty);
            var watchers = await _repository.GetTaskWatchersAsync(taskId);
            var feedRelatedRecords = new List<string> { taskId.ToString(), record.Id.ToString() };
            feedRelatedRecords.AddRange(watchers.Select(w => w.ToString()));

            Models.Project? project = await ResolveProjectForTaskAsync(relatedTask);
            if (project != null)
            {
                feedRelatedRecords.Add(project.Id.ToString());
            }

            await CreateFeedItemAsync(
                id: null,
                createdBy: currentUserId,
                createdOn: DateTime.UtcNow,
                subject: feedSubject,
                body: feedBody,
                relatedRecords: feedRelatedRecords,
                scope: new List<string> { "projects" },
                type: "timelog");

            // Step 6: Publish SNS event
            await PublishDomainEventAsync("inventory.timelog.created", new
            {
                timelogId = record.Id,
                taskId,
                minutes = record.Minutes,
                isBillable = record.IsBillable,
                createdBy = currentUserId,
                timestamp = DateTime.UtcNow
            });
        }

        /// <inheritdoc />
        public async Task HandleTimelogDeletionHookAsync(Guid recordId)
        {
            _logger.LogInformation("Handling timelog deletion hook for timelog {TimelogId}", recordId);

            // Step 1: Load the timelog record (source lines 292-298)
            var timelog = await _repository.GetTimelogByIdAsync(recordId);
            if (timelog == null)
            {
                _logger.LogWarning("Timelog {TimelogId} not found for deletion hook", recordId);
                return;
            }

            // Step 2: Check scope contains "projects" (source lines 299-302)
            List<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(timelog.LScope))
            {
                scope = JsonConvert.DeserializeObject<List<string>>(timelog.LScope);
            }
            if (scope == null || !scope.Contains("projects"))
            {
                return;
            }

            // Step 3: Find related task (source lines 307-324)
            List<Guid>? relatedRecordIds = null;
            if (!string.IsNullOrWhiteSpace(timelog.LRelatedRecords))
            {
                relatedRecordIds = JsonConvert.DeserializeObject<List<Guid>>(timelog.LRelatedRecords);
            }

            if (relatedRecordIds != null && relatedRecordIds.Count > 0)
            {
                foreach (var relId in relatedRecordIds)
                {
                    var relatedTask = await _repository.GetTaskByIdAsync(relId);
                    if (relatedTask != null)
                    {
                        // Step 4: Reverse aggregate minutes (source lines 332-353)
                        if (timelog.IsBillable)
                        {
                            relatedTask.XBillableMinutes = Math.Max(0, relatedTask.XBillableMinutes - timelog.Minutes);
                        }
                        else
                        {
                            relatedTask.XNonBillableMinutes = Math.Max(0, relatedTask.XNonBillableMinutes - timelog.Minutes);
                        }
                        await _repository.UpdateTaskAsync(relatedTask);
                        _logger.LogInformation("Reversed {Minutes} minutes on task {TaskId}", timelog.Minutes, relId);
                        break;
                    }
                }
            }

            // Step 5: Delete related feed items (source lines 356-364)
            var relatedFeedItems = await _repository.GetFeedItemsByRelatedRecordAsync(recordId.ToString());
            foreach (var feedItem in relatedFeedItems)
            {
                await _repository.DeleteFeedItemAsync(feedItem.Id);
                _logger.LogInformation("Deleted feed item {FeedItemId} related to timelog {TimelogId}", feedItem.Id, recordId);
            }

            // Step 6: Publish SNS event
            await PublishDomainEventAsync("inventory.timelog.deleted", new
            {
                timelogId = recordId,
                minutes = timelog.Minutes,
                isBillable = timelog.IsBillable,
                timestamp = DateTime.UtcNow
            });
        }

        /// <inheritdoc />
        public async Task HandleTrackTimePagePostAsync(
            Guid taskId,
            int minutes,
            DateTime loggedOn,
            bool isBillable,
            string body,
            Guid currentUserId)
        {
            _logger.LogInformation("Handling track time post for task {TaskId}, {Minutes} minutes by user {UserId}",
                taskId, minutes, currentUserId);

            // Step 1: Load the task to get project context (source lines 115-123)
            var task = await _repository.GetTaskByIdAsync(taskId);
            if (task == null)
            {
                throw new InvalidOperationException($"Task with ID {taskId} was not found.");
            }

            // Step 2: Stop active timelog on task (source line 149)
            await StopTaskTimelogAsync(taskId);

            // Step 3: Create new timelog if minutes > 0 (source lines 161-163)
            if (minutes > 0)
            {
                Models.Project? project = await ResolveProjectForTaskAsync(task);
                var relatedRecords = new List<Guid> { taskId };
                if (project != null)
                {
                    relatedRecords.Add(project.Id);
                }

                await CreateTimelogAsync(
                    id: null,
                    createdBy: currentUserId,
                    createdOn: DateTime.UtcNow,
                    loggedOn: loggedOn,
                    minutes: minutes,
                    isBillable: isBillable,
                    body: body ?? string.Empty,
                    scope: new List<string> { "projects" },
                    relatedRecords: relatedRecords);

                _logger.LogInformation("Created timelog of {Minutes} minutes for task {TaskId}", minutes, taskId);
            }
            else
            {
                _logger.LogInformation("Skipped timelog creation (0 minutes) for task {TaskId}", taskId);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  COMMENT OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task CreateCommentAsync(
            Guid? id,
            Guid? createdBy,
            DateTime? createdOn,
            string body,
            Guid? parentId,
            List<string> scope,
            List<Guid> relatedRecords)
        {
            var commentId = id ?? Guid.NewGuid();
            var effectiveCreatedBy = createdBy ?? SystemUserId;
            var effectiveCreatedOn = createdOn ?? DateTime.UtcNow;

            _logger.LogInformation("Creating comment {CommentId} by user {CreatedBy}", commentId, effectiveCreatedBy);

            var comment = new Models.Comment
            {
                Id = commentId,
                CreatedBy = effectiveCreatedBy,
                CreatedOn = effectiveCreatedOn,
                Body = body ?? string.Empty,
                ParentId = parentId,
                LScope = JsonConvert.SerializeObject(scope ?? new List<string>()),
                LRelatedRecords = JsonConvert.SerializeObject(relatedRecords ?? new List<Guid>())
            };

            await _repository.CreateCommentAsync(comment);
            _logger.LogInformation("Comment {CommentId} created successfully", commentId);
        }

        /// <inheritdoc />
        public async Task DeleteCommentAsync(Guid recordId, Guid currentUserId)
        {
            _logger.LogInformation("Deleting comment {CommentId} by user {UserId}", recordId, currentUserId);

            // Step 1: Validate author-only deletion (source lines 57-63)
            var comment = await _repository.GetCommentByIdAsync(recordId);
            if (comment == null)
            {
                throw new InvalidOperationException($"Comment with ID {recordId} was not found.");
            }

            if (comment.CreatedBy != currentUserId)
            {
                throw new InvalidOperationException("Only the author can delete this comment.");
            }

            // Step 2: Delete child comments first (one level nesting, source lines 72-97)
            var childComments = await _repository.GetCommentsByParentAsync(recordId);
            foreach (var child in childComments)
            {
                await _repository.DeleteCommentAsync(child.Id);
                _logger.LogInformation("Deleted child comment {ChildId} of parent {ParentId}", child.Id, recordId);
            }

            // Step 3: Delete the parent comment
            await _repository.DeleteCommentAsync(recordId);
            _logger.LogInformation("Comment {CommentId} deleted with {ChildCount} child comments", recordId, childComments.Count);

            // Step 4: Publish SNS event
            await PublishDomainEventAsync("inventory.comment.deleted", new
            {
                commentId = recordId,
                deletedBy = currentUserId,
                childCount = childComments.Count,
                timestamp = DateTime.UtcNow
            });
        }

        /// <inheritdoc />
        public async Task HandleCommentCreationPreHookAsync(
            string entityName,
            Models.Comment record,
            Guid currentUserId,
            List<string> errors)
        {
            _logger.LogInformation("Handling comment pre-create hook for entity {Entity}, comment {CommentId}",
                entityName, record.Id);

            // Step 1: Check if project-scoped comment (source lines 108-111)
            List<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(record.LScope))
            {
                scope = JsonConvert.DeserializeObject<List<string>>(record.LScope);
            }
            if (scope == null || !scope.Contains("projects"))
            {
                return;
            }

            // Step 2: Load related tasks with project and watcher data (source lines 116-138)
            List<Guid>? relatedRecordIds = null;
            if (!string.IsNullOrWhiteSpace(record.LRelatedRecords))
            {
                relatedRecordIds = JsonConvert.DeserializeObject<List<Guid>>(record.LRelatedRecords);
            }

            if (relatedRecordIds == null || relatedRecordIds.Count == 0)
            {
                errors.Add("A project comment must have at least one related task.");
                return;
            }

            // Find the related task (source lines 126-130)
            Models.Task? relatedTask = null;
            Guid taskId = Guid.Empty;
            foreach (var relId in relatedRecordIds)
            {
                var candidate = await _repository.GetTaskByIdAsync(relId);
                if (candidate != null)
                {
                    relatedTask = candidate;
                    taskId = relId;
                    break;
                }
            }

            if (relatedTask == null)
            {
                errors.Add("No valid task found in related records for this comment.");
                return;
            }

            // Step 3: Create activity feed item (source lines 162-174)
            string key = relatedTask.Key ?? relatedTask.Id.ToString();
            string taskSubject = relatedTask.Subject ?? string.Empty;
            var feedSubject = $"commented on <a href=\"/projects/tasks/tasks/r/{taskId}/details\">[{key}] {taskSubject}</a>";
            var feedBody = GetSnippetFromHtml(record.Body);

            var watchers = await _repository.GetTaskWatchersAsync(taskId);
            Models.Project? project = await ResolveProjectForTaskAsync(relatedTask);

            var feedRelatedRecords = new List<string> { taskId.ToString(), record.Id.ToString() };
            if (project != null)
            {
                feedRelatedRecords.Add(project.Id.ToString());
            }
            feedRelatedRecords.AddRange(watchers.Select(w => w.ToString()));

            await CreateFeedItemAsync(
                id: null,
                createdBy: currentUserId,
                createdOn: DateTime.UtcNow,
                subject: feedSubject,
                body: feedBody,
                relatedRecords: feedRelatedRecords,
                scope: new List<string> { "projects" },
                type: "comment");

            // Step 4: Publish SNS event
            await PublishDomainEventAsync("inventory.comment.created", new
            {
                commentId = record.Id,
                taskId,
                createdBy = currentUserId,
                timestamp = DateTime.UtcNow
            });
        }

        /// <inheritdoc />
        public async Task HandleCommentCreationPostHookAsync(
            string entityName,
            Models.Comment record,
            Guid currentUserId)
        {
            _logger.LogInformation("Handling comment post-create hook for entity {Entity}, comment {CommentId}",
                entityName, record.Id);

            // Add comment author to task watchers if not already present (source lines 211-221)
            List<string>? scope = null;
            if (!string.IsNullOrWhiteSpace(record.LScope))
            {
                scope = JsonConvert.DeserializeObject<List<string>>(record.LScope);
            }
            if (scope == null || !scope.Contains("projects"))
            {
                return;
            }

            List<Guid>? relatedRecordIds = null;
            if (!string.IsNullOrWhiteSpace(record.LRelatedRecords))
            {
                relatedRecordIds = JsonConvert.DeserializeObject<List<Guid>>(record.LRelatedRecords);
            }

            if (relatedRecordIds == null || relatedRecordIds.Count == 0)
            {
                return;
            }

            foreach (var relId in relatedRecordIds)
            {
                var task = await _repository.GetTaskByIdAsync(relId);
                if (task != null)
                {
                    var watchers = await _repository.GetTaskWatchersAsync(relId);
                    if (!watchers.Contains(currentUserId))
                    {
                        await _repository.AddTaskWatcherAsync(relId, currentUserId);
                        _logger.LogInformation("Added comment author {UserId} as watcher on task {TaskId}",
                            currentUserId, relId);
                    }
                    break;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  FEED ITEM OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task CreateFeedItemAsync(
            Guid? id,
            Guid? createdBy,
            DateTime? createdOn,
            string subject,
            string body,
            List<string> relatedRecords,
            List<string> scope,
            string type)
        {
            var feedItemId = id ?? Guid.NewGuid();
            var effectiveCreatedBy = createdBy ?? SystemUserId;
            var effectiveCreatedOn = createdOn ?? DateTime.UtcNow;
            var effectiveType = string.IsNullOrWhiteSpace(type) ? "system" : type;

            _logger.LogInformation("Creating feed item {FeedItemId} of type {Type}", feedItemId, effectiveType);

            var feedItem = new Models.FeedItem
            {
                Id = feedItemId,
                CreatedBy = effectiveCreatedBy,
                CreatedOn = effectiveCreatedOn,
                Subject = subject ?? string.Empty,
                Body = body ?? string.Empty,
                Type = effectiveType,
                LRelatedRecords = JsonConvert.SerializeObject(relatedRecords ?? new List<string>()),
                LScope = JsonConvert.SerializeObject(scope ?? new List<string>())
            };

            await _repository.CreateFeedItemAsync(feedItem);
            _logger.LogInformation("Feed item {FeedItemId} created successfully", feedItemId);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PROJECT OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<Models.Project> GetProjectAsync(Guid projectId)
        {
            _logger.LogInformation("Retrieving project {ProjectId}", projectId);

            var project = await _repository.GetProjectByIdAsync(projectId);
            if (project == null)
            {
                throw new InvalidOperationException($"Error: No project was found for ProjectId {projectId}");
            }

            return project;
        }

        /// <inheritdoc />
        public async Task<List<Models.Timelog>> GetProjectTimelogsAsync(Guid projectId)
        {
            _logger.LogInformation("Retrieving timelogs for project {ProjectId}", projectId);

            // Query timelogs filtering by project (source lines 25-33)
            // Uses repository date range with wide window and project filter
            var timelogs = await _repository.GetTimelogsByDateRangeAsync(
                DateTime.MinValue.AddYears(1),
                DateTime.MaxValue.AddYears(-1),
                userId: null,
                projectId: projectId);

            _logger.LogInformation("Retrieved {Count} timelogs for project {ProjectId}", timelogs.Count, projectId);
            return timelogs;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  REPORT OPERATIONS
        // ═══════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        public async Task<List<Dictionary<string, object>>> GetTimelogReportDataAsync(int year, int month, Guid? accountId)
        {
            _logger.LogInformation("Generating timelog report for {Year}/{Month}, accountId={AccountId}", year, month, accountId);

            // Validate parameters (source lines 17-21)
            if (month < 1 || month > 12)
            {
                throw new InvalidOperationException("Month must be between 1 and 12.");
            }
            if (year < 1)
            {
                throw new InvalidOperationException("Year must be a positive number.");
            }

            // Calculate date range for the month (source lines 35-38)
            var fromDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var toDate = fromDate.AddMonths(1);

            // Step 1: Load timelogs for date range with "projects" scope (source lines 39-46)
            var timelogs = await _repository.GetTimelogsByDateRangeAsync(fromDate, toDate);
            timelogs = timelogs.Where(t =>
            {
                if (string.IsNullOrWhiteSpace(t.LScope)) return false;
                var s = JsonConvert.DeserializeObject<List<string>>(t.LScope);
                return s != null && s.Contains("projects");
            }).ToList();

            if (timelogs.Count == 0)
            {
                return new List<Dictionary<string, object>>();
            }

            // Step 2: Extract task IDs from timelog l_related_records (source lines 48-56)
            var taskTimelogMap = new Dictionary<Guid, List<Models.Timelog>>();
            foreach (var timelog in timelogs)
            {
                if (string.IsNullOrWhiteSpace(timelog.LRelatedRecords)) continue;
                var relatedIds = JsonConvert.DeserializeObject<List<Guid>>(timelog.LRelatedRecords);
                if (relatedIds == null || relatedIds.Count == 0) continue;

                // First related record is typically the task ID (source line 55)
                var taskIdCandidate = relatedIds.FirstOrDefault();
                if (taskIdCandidate == Guid.Empty) continue;

                if (!taskTimelogMap.ContainsKey(taskIdCandidate))
                {
                    taskTimelogMap[taskIdCandidate] = new List<Models.Timelog>();
                }
                taskTimelogMap[taskIdCandidate].Add(timelog);
            }

            // Step 3: Load all referenced tasks (source lines 59-61)
            var result = new List<Dictionary<string, object>>();
            foreach (var kvp in taskTimelogMap)
            {
                var task = await _repository.GetTaskByIdAsync(kvp.Key);
                if (task == null) continue;

                // Resolve project for this task
                Models.Project? project = await ResolveProjectForTaskAsync(task);

                // Filter by accountId if specified (source lines 24-29)
                if (accountId.HasValue && project != null && project.AccountId != accountId.Value)
                {
                    continue;
                }

                // Resolve task type label
                string taskTypeLabel = string.Empty;
                if (task.TypeId.HasValue)
                {
                    var allTypes = await _repository.GetAllTaskTypesAsync();
                    var taskType = allTypes.FirstOrDefault(tt => tt.Id == task.TypeId.Value);
                    taskTypeLabel = taskType?.Label ?? string.Empty;
                }

                // Step 4: Aggregate billable/non-billable minutes (source lines 103-132)
                decimal billableMinutes = 0;
                decimal nonBillableMinutes = 0;
                foreach (var tl in kvp.Value)
                {
                    if (tl.IsBillable)
                    {
                        billableMinutes += tl.Minutes;
                    }
                    else
                    {
                        nonBillableMinutes += tl.Minutes;
                    }
                }

                result.Add(new Dictionary<string, object>
                {
                    ["task_id"] = task.Id,
                    ["project_id"] = project?.Id ?? Guid.Empty,
                    ["task_subject"] = task.Subject ?? string.Empty,
                    ["project_name"] = project?.Name ?? string.Empty,
                    ["task_type"] = taskTypeLabel,
                    ["billable_minutes"] = billableMinutes,
                    ["non_billable_minutes"] = nonBillableMinutes
                });
            }

            _logger.LogInformation("Timelog report generated with {Count} task entries", result.Count);
            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PRIVATE HELPER METHODS
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the project associated with a task by parsing its LRelatedRecords
        /// and querying the repository. Replaces the monolith's EQL $project_nn_task join.
        /// </summary>
        private async Task<Models.Project?> ResolveProjectForTaskAsync(Models.Task task)
        {
            if (string.IsNullOrWhiteSpace(task.LRelatedRecords))
            {
                return null;
            }

            List<Guid>? relatedRecords;
            try
            {
                relatedRecords = JsonConvert.DeserializeObject<List<Guid>>(task.LRelatedRecords);
            }
            catch
            {
                return null;
            }

            if (relatedRecords == null)
            {
                return null;
            }

            foreach (var candidateId in relatedRecords)
            {
                var project = await _repository.GetProjectByIdAsync(candidateId);
                if (project != null)
                {
                    return project;
                }
            }

            return null;
        }

        /// <summary>
        /// Publishes a domain event to the Inventory SNS topic.
        /// Event naming convention: {domain}.{entity}.{action} per AAP §0.8.5.
        /// Replaces the monolith's synchronous in-process HookManager post-hooks.
        /// </summary>
        private async Task PublishDomainEventAsync(string eventType, object eventData)
        {
            try
            {
                var message = System.Text.Json.JsonSerializer.Serialize(eventData);
                var request = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = message,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        }
                    }
                };

                await _snsClient.PublishAsync(request);
                _logger.LogInformation("Published domain event {EventType} to SNS topic", eventType);
            }
            catch (Exception ex)
            {
                // Log but do not throw — event publishing failures should not break the primary operation
                _logger.LogError(ex, "Failed to publish domain event {EventType}", eventType);
            }
        }

        /// <summary>
        /// Strips HTML tags from content and truncates to a maximum snippet length.
        /// Replaces the monolith's RenderService.GetSnippetFromHtml used in feed item creation
        /// for comments, tasks, and timelogs.
        /// </summary>
        private static string GetSnippetFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            // Strip all HTML tags using regex
            var plainText = Regex.Replace(html, "<[^>]*>", string.Empty);

            // Decode common HTML entities
            plainText = plainText
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&nbsp;", " ");

            // Trim whitespace and truncate
            plainText = plainText.Trim();
            if (plainText.Length > SnippetMaxLength)
            {
                plainText = plainText.Substring(0, SnippetMaxLength) + "...";
            }

            return plainText;
        }
    }
}
