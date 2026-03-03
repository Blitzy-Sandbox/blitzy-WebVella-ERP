using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Project.Fixtures
{
    /// <summary>
    /// Contains Builder-pattern classes for creating <see cref="EntityRecord"/> instances
    /// that match the exact field structures expected by the Project service domain entities.
    /// Each builder provides fluent configuration methods with sensible defaults and produces
    /// EntityRecord objects populated via string-indexed property access, matching the monolith's
    /// dynamic entity-field model.
    ///
    /// Builders included:
    /// <list type="bullet">
    ///   <item><see cref="TaskRecordBuilder"/> — Task entity (from TaskService.cs)</item>
    ///   <item><see cref="TimelogRecordBuilder"/> — Timelog entity (from TimeLogService.cs)</item>
    ///   <item><see cref="CommentRecordBuilder"/> — Comment entity (from CommentService.cs)</item>
    ///   <item><see cref="FeedItemRecordBuilder"/> — Feed item entity (from FeedItemService.cs)</item>
    ///   <item><see cref="ProjectRecordBuilder"/> — Project entity (from ProjectService.cs/ReportService.cs)</item>
    /// </list>
    /// </summary>

    // =========================================================================
    // TaskRecordBuilder — Task entity builder
    // Source: WebVella.Erp.Plugins.Project/Services/TaskService.cs
    // Fields: id, subject, number, key, priority, status_id, type_id,
    //         start_time, end_time, owner_id, created_by, created_on,
    //         body, timelog_started_on, x_search
    // =========================================================================

    /// <summary>
    /// Fluent builder for creating <see cref="EntityRecord"/> instances representing
    /// task entities. Field names and types match the exact structure used in
    /// TaskService.SetCalculationFields(), GetTask(), GetPageHookLogic(),
    /// StartTaskTimelog(), and PreCreateRecordPageHookLogic().
    /// </summary>
    public class TaskRecordBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _subject = "Default Task Subject";
        private decimal _number = 1;
        private string _key = "";
        private string _priority = "1";
        private Guid? _statusId = null;
        private Guid? _typeId = null;
        private DateTime? _startTime = DateTime.UtcNow.Date;
        private DateTime? _endTime = DateTime.UtcNow.Date.AddDays(7);
        private Guid? _ownerId = SystemIds.SystemUserId;
        private Guid _createdBy = SystemIds.SystemUserId;
        private DateTime _createdOn = DateTime.UtcNow;
        private string _body = "";
        private DateTime? _timelogStartedOn = null;
        private string _xSearch = "";

        /// <summary>Sets the unique identifier for the task record.</summary>
        /// <param name="id">A <see cref="Guid"/> representing the task ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the subject/title of the task.</summary>
        /// <param name="subject">The task subject string.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithSubject(string subject) { _subject = subject; return this; }

        /// <summary>Sets the sequential task number within the project.</summary>
        /// <param name="number">A decimal task number (e.g., 1, 2, 3).</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithNumber(decimal number) { _number = number; return this; }

        /// <summary>
        /// Sets the display key for the task (e.g., "PRJ-1").
        /// If not set, Build() generates a default key as "TST-{number}".
        /// In the monolith, TaskService.SetCalculationFields() computes key as
        /// projectAbbr + "-" + number.ToString("N0").
        /// </summary>
        /// <param name="key">The task key string.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithKey(string key) { _key = key; return this; }

        /// <summary>
        /// Sets the priority value of the task.
        /// In the monolith, priority is a string value used for icon/color lookup
        /// via TaskService.GetTaskIconAndColor().
        /// </summary>
        /// <param name="priority">The priority value string (e.g., "1", "2").</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithPriority(string priority) { _priority = priority; return this; }

        /// <summary>Sets the status entity reference ID for the task.</summary>
        /// <param name="statusId">A nullable <see cref="Guid"/> for the task status.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithStatusId(Guid? statusId) { _statusId = statusId; return this; }

        /// <summary>Sets the type entity reference ID for the task.</summary>
        /// <param name="typeId">A nullable <see cref="Guid"/> for the task type.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithTypeId(Guid? typeId) { _typeId = typeId; return this; }

        /// <summary>
        /// Sets the start time of the task.
        /// In the monolith, TaskService.GetPageHookLogic() sets this to
        /// DateTime.Now.Date.ClearKind().
        /// </summary>
        /// <param name="startTime">A nullable <see cref="DateTime"/> for the start time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithStartTime(DateTime? startTime) { _startTime = startTime; return this; }

        /// <summary>
        /// Sets the end time of the task.
        /// In the monolith, TaskService.GetPageHookLogic() sets this to
        /// DateTime.Now.Date.ClearKind().AddDays(1).
        /// </summary>
        /// <param name="endTime">A nullable <see cref="DateTime"/> for the end time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithEndTime(DateTime? endTime) { _endTime = endTime; return this; }

        /// <summary>
        /// Sets the owner user ID for the task.
        /// In the monolith, TaskService.GetPageHookLogic() sets this to
        /// currentUser.Id. Default is SystemIds.SystemUserId.
        /// </summary>
        /// <param name="ownerId">A nullable <see cref="Guid"/> for the owner user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithOwnerId(Guid? ownerId) { _ownerId = ownerId; return this; }

        /// <summary>Sets the ID of the user who created the task.</summary>
        /// <param name="createdBy">A <see cref="Guid"/> for the creator user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithCreatedBy(Guid createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the creation timestamp of the task.</summary>
        /// <param name="createdOn">A <see cref="DateTime"/> for the creation time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>Sets the body/description content of the task.</summary>
        /// <param name="body">The task body text.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithBody(string body) { _body = body; return this; }

        /// <summary>
        /// Sets the timelog started timestamp.
        /// In the monolith, TaskService.StartTaskTimelog() sets this to DateTime.Now,
        /// and StopTaskTimelog() sets it to null.
        /// </summary>
        /// <param name="timelogStartedOn">A nullable <see cref="DateTime"/> for the timelog start.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TaskRecordBuilder WithTimelogStartedOn(DateTime? timelogStartedOn) { _timelogStartedOn = timelogStartedOn; return this; }

        /// <summary>
        /// Builds and returns an <see cref="EntityRecord"/> populated with all configured
        /// task fields. The key field is auto-generated as "TST-{number}" if not
        /// explicitly set, following the monolith pattern of projectAbbr + "-" + number.
        /// </summary>
        /// <returns>A fully populated <see cref="EntityRecord"/> representing a task.</returns>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["subject"] = _subject;
            record["number"] = _number;
            // Key generation follows the monolith pattern from TaskService.SetCalculationFields():
            // patchRecord["key"] = projectAbbr + "-" + ((decimal)taskRecord["number"]).ToString("N0");
            // For tests, we use "TST-" as a default project abbreviation prefix.
            record["key"] = string.IsNullOrEmpty(_key) ? "TST-" + _number.ToString("N0") : _key;
            record["priority"] = _priority;
            record["status_id"] = _statusId;
            record["type_id"] = _typeId;
            record["start_time"] = _startTime;
            record["end_time"] = _endTime;
            record["owner_id"] = _ownerId;
            record["created_by"] = _createdBy;
            record["created_on"] = _createdOn;
            record["body"] = _body;
            record["timelog_started_on"] = _timelogStartedOn;
            record["x_search"] = _xSearch;
            return record;
        }
    }

    // =========================================================================
    // TimelogRecordBuilder — Timelog entity builder
    // Source: WebVella.Erp.Plugins.Project/Services/TimeLogService.cs
    // Fields: id, created_by, created_on, logged_on, minutes, is_billable,
    //         body, l_scope (JSON), l_related_records (JSON)
    // =========================================================================

    /// <summary>
    /// Fluent builder for creating <see cref="EntityRecord"/> instances representing
    /// timelog entities. Field names and types match the exact structure used in
    /// TimeLogService.Create() — specifically lines 39-48 of the source.
    ///
    /// JSON-serialized fields (l_scope, l_related_records) use
    /// <see cref="JsonConvert.SerializeObject(object)"/> matching the monolith pattern.
    /// </summary>
    public class TimelogRecordBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid _createdBy = SystemIds.SystemUserId;
        private DateTime _createdOn = DateTime.UtcNow;
        private DateTime _loggedOn = DateTime.UtcNow;
        private int _minutes = 60;
        private bool _isBillable = true;
        private string _body = "";
        private List<string> _scope = new List<string> { "projects" };
        private List<Guid> _relatedRecords = new List<Guid>();

        /// <summary>Sets the unique identifier for the timelog record.</summary>
        /// <param name="id">A <see cref="Guid"/> representing the timelog ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the ID of the user who created the timelog entry.</summary>
        /// <param name="createdBy">A <see cref="Guid"/> for the creator user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithCreatedBy(Guid createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the creation timestamp of the timelog entry.</summary>
        /// <param name="createdOn">A <see cref="DateTime"/> for the creation time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>
        /// Sets the date/time when the work was logged.
        /// In the monolith, TimeLogService.Create() applies ConvertAppDateToUtc() to this value.
        /// </summary>
        /// <param name="loggedOn">A <see cref="DateTime"/> for the logged-on time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithLoggedOn(DateTime loggedOn) { _loggedOn = loggedOn; return this; }

        /// <summary>Sets the number of minutes logged.</summary>
        /// <param name="minutes">The number of minutes worked.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithMinutes(int minutes) { _minutes = minutes; return this; }

        /// <summary>Sets whether the logged time is billable.</summary>
        /// <param name="isBillable">True if the time is billable; false otherwise.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithIsBillable(bool isBillable) { _isBillable = isBillable; return this; }

        /// <summary>Sets the body/description of the timelog entry.</summary>
        /// <param name="body">The timelog body text.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithBody(string body) { _body = body; return this; }

        /// <summary>
        /// Sets the scope tags for the timelog entry.
        /// In the monolith, this is serialized to JSON and stored in the "l_scope" field.
        /// Default value is ["projects"], matching the standard project scope.
        /// </summary>
        /// <param name="scope">A list of scope tag strings.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithScope(List<string> scope) { _scope = scope; return this; }

        /// <summary>
        /// Sets the related record IDs for the timelog entry.
        /// In the monolith, this is <see cref="List{Guid}"/> serialized to JSON and stored
        /// in the "l_related_records" field. Typically contains the task ID that the timelog
        /// is associated with, used by ReportService to correlate timelogs to tasks.
        /// </summary>
        /// <param name="relatedRecords">A list of related record GUIDs.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public TimelogRecordBuilder WithRelatedRecords(List<Guid> relatedRecords) { _relatedRecords = relatedRecords; return this; }

        /// <summary>
        /// Builds and returns an <see cref="EntityRecord"/> populated with all configured
        /// timelog fields. The l_scope and l_related_records fields are JSON-serialized
        /// using Newtonsoft.Json, matching the monolith pattern in TimeLogService.Create().
        /// </summary>
        /// <returns>A fully populated <see cref="EntityRecord"/> representing a timelog.</returns>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["created_by"] = _createdBy;
            record["created_on"] = _createdOn;
            record["logged_on"] = _loggedOn;
            record["minutes"] = _minutes;
            record["is_billable"] = _isBillable;
            record["body"] = _body;
            // JSON serialization matches monolith TimeLogService.cs lines 47-48:
            // record["l_scope"] = JsonConvert.SerializeObject(scope);
            // record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);
            record["l_scope"] = JsonConvert.SerializeObject(_scope);
            record["l_related_records"] = JsonConvert.SerializeObject(_relatedRecords);
            return record;
        }
    }

    // =========================================================================
    // CommentRecordBuilder — Comment entity builder
    // Source: WebVella.Erp.Plugins.Project/Services/CommentService.cs
    // Fields: id, created_by, created_on, body, parent_id,
    //         l_scope (JSON), l_related_records (JSON)
    // =========================================================================

    /// <summary>
    /// Fluent builder for creating <see cref="EntityRecord"/> instances representing
    /// comment entities. Field names and types match the exact structure used in
    /// CommentService.Create() — specifically lines 32-38 of the source.
    ///
    /// JSON-serialized fields (l_scope, l_related_records) use
    /// <see cref="JsonConvert.SerializeObject(object)"/> matching the monolith pattern.
    /// </summary>
    public class CommentRecordBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid _createdBy = SystemIds.SystemUserId;
        private DateTime _createdOn = DateTime.UtcNow;
        private string _body = "Default comment body";
        private Guid? _parentId = null;
        private List<string> _scope = new List<string> { "projects" };
        private List<Guid> _relatedRecords = new List<Guid>();

        /// <summary>Sets the unique identifier for the comment record.</summary>
        /// <param name="id">A <see cref="Guid"/> representing the comment ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the ID of the user who created the comment.</summary>
        /// <param name="createdBy">A <see cref="Guid"/> for the creator user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithCreatedBy(Guid createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the creation timestamp of the comment.</summary>
        /// <param name="createdOn">A <see cref="DateTime"/> for the creation time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>Sets the body/content of the comment.</summary>
        /// <param name="body">The comment body text.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithBody(string body) { _body = body; return this; }

        /// <summary>
        /// Sets the parent comment ID for threaded/nested comments.
        /// Null indicates a top-level comment.
        /// </summary>
        /// <param name="parentId">A nullable <see cref="Guid"/> for the parent comment.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithParentId(Guid? parentId) { _parentId = parentId; return this; }

        /// <summary>
        /// Sets the scope tags for the comment.
        /// In the monolith, this is serialized to JSON and stored in the "l_scope" field.
        /// Default value is ["projects"], matching the standard project scope.
        /// </summary>
        /// <param name="scope">A list of scope tag strings.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithScope(List<string> scope) { _scope = scope; return this; }

        /// <summary>
        /// Sets the related record IDs for the comment.
        /// In the monolith, this is <see cref="List{Guid}"/> serialized to JSON and stored
        /// in the "l_related_records" field. Typically contains the task ID that the
        /// comment is associated with.
        /// </summary>
        /// <param name="relatedRecords">A list of related record GUIDs.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public CommentRecordBuilder WithRelatedRecords(List<Guid> relatedRecords) { _relatedRecords = relatedRecords; return this; }

        /// <summary>
        /// Builds and returns an <see cref="EntityRecord"/> populated with all configured
        /// comment fields. The l_scope and l_related_records fields are JSON-serialized
        /// using Newtonsoft.Json, matching the monolith pattern in CommentService.Create().
        /// </summary>
        /// <returns>A fully populated <see cref="EntityRecord"/> representing a comment.</returns>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["created_by"] = _createdBy;
            record["created_on"] = _createdOn;
            record["body"] = _body;
            record["parent_id"] = _parentId;
            // JSON serialization matches monolith CommentService.cs lines 37-38:
            // record["l_scope"] = JsonConvert.SerializeObject(scope);
            // record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);
            record["l_scope"] = JsonConvert.SerializeObject(_scope);
            record["l_related_records"] = JsonConvert.SerializeObject(_relatedRecords);
            return record;
        }
    }

    // =========================================================================
    // FeedItemRecordBuilder — Feed item entity builder
    // Source: WebVella.Erp.Plugins.Project/Services/FeedItemService.cs
    // Fields: id, created_by, created_on, subject, body, type,
    //         l_related_records (JSON), l_scope (JSON)
    //
    // IMPORTANT: relatedRecords is List<string>, NOT List<Guid>.
    // This matches FeedItemService.Create() signature exactly (line 16).
    // =========================================================================

    /// <summary>
    /// Fluent builder for creating <see cref="EntityRecord"/> instances representing
    /// feed item entities. Field names and types match the exact structure used in
    /// FeedItemService.Create() — specifically lines 35-43 of the source.
    ///
    /// <b>IMPORTANT:</b> The relatedRecords field uses <see cref="List{String}"/>,
    /// NOT <see cref="List{Guid}"/>. This matches the FeedItemService.Create()
    /// method signature exactly, which differs from TimeLogService and CommentService.
    ///
    /// JSON-serialized fields (l_related_records, l_scope) use
    /// <see cref="JsonConvert.SerializeObject(object)"/> matching the monolith pattern.
    /// </summary>
    public class FeedItemRecordBuilder
    {
        private Guid _id = Guid.NewGuid();
        private Guid _createdBy = SystemIds.SystemUserId;
        private DateTime _createdOn = DateTime.UtcNow;
        private string _subject = "Default feed subject";
        private string _body = "";
        private string _type = "system";
        private List<string> _relatedRecords = new List<string>();
        private List<string> _scope = new List<string> { "projects" };

        /// <summary>Sets the unique identifier for the feed item record.</summary>
        /// <param name="id">A <see cref="Guid"/> representing the feed item ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the ID of the user who created the feed item.</summary>
        /// <param name="createdBy">A <see cref="Guid"/> for the creator user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithCreatedBy(Guid createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the creation timestamp of the feed item.</summary>
        /// <param name="createdOn">A <see cref="DateTime"/> for the creation time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>Sets the subject/title of the feed item.</summary>
        /// <param name="subject">The feed item subject string.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithSubject(string subject) { _subject = subject; return this; }

        /// <summary>Sets the body/content of the feed item.</summary>
        /// <param name="body">The feed item body text.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithBody(string body) { _body = body; return this; }

        /// <summary>
        /// Sets the feed item type classification.
        /// In the monolith, FeedItemService.Create() defaults to "system" when
        /// the type parameter is null or whitespace (line 29-31).
        /// </summary>
        /// <param name="type">The feed item type string (e.g., "system", "comment").</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithType(string type) { _type = type; return this; }

        /// <summary>
        /// Sets the related record references for the feed item.
        /// <b>IMPORTANT:</b> This is <see cref="List{String}"/>, NOT <see cref="List{Guid}"/>,
        /// matching the FeedItemService.Create() method signature exactly.
        /// In the monolith, this is serialized to JSON and stored in "l_related_records".
        /// </summary>
        /// <param name="relatedRecords">A list of related record string references.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithRelatedRecords(List<string> relatedRecords) { _relatedRecords = relatedRecords; return this; }

        /// <summary>
        /// Sets the scope tags for the feed item.
        /// In the monolith, this is serialized to JSON and stored in the "l_scope" field.
        /// Default value is ["projects"], matching the standard project scope.
        /// </summary>
        /// <param name="scope">A list of scope tag strings.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public FeedItemRecordBuilder WithScope(List<string> scope) { _scope = scope; return this; }

        /// <summary>
        /// Builds and returns an <see cref="EntityRecord"/> populated with all configured
        /// feed item fields. The l_related_records and l_scope fields are JSON-serialized
        /// using Newtonsoft.Json, matching the monolith pattern in FeedItemService.Create().
        /// Note: The field assignment order matches the source (lines 36-43).
        /// </summary>
        /// <returns>A fully populated <see cref="EntityRecord"/> representing a feed item.</returns>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["created_by"] = _createdBy;
            record["created_on"] = _createdOn;
            record["subject"] = _subject;
            record["body"] = _body;
            // JSON serialization matches monolith FeedItemService.cs lines 41-42:
            // record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);
            // record["l_scope"] = JsonConvert.SerializeObject(scope);
            record["l_related_records"] = JsonConvert.SerializeObject(_relatedRecords);
            record["l_scope"] = JsonConvert.SerializeObject(_scope);
            record["type"] = _type;
            return record;
        }
    }

    // =========================================================================
    // ProjectRecordBuilder — Project entity builder
    // Source: WebVella.Erp.Plugins.Project/Services/ProjectService.cs,
    //         WebVella.Erp.Plugins.Project/Services/ReportService.cs
    // Fields: id, name, abbr, owner_id, account_id, is_billable,
    //         created_by, created_on
    // =========================================================================

    /// <summary>
    /// Fluent builder for creating <see cref="EntityRecord"/> instances representing
    /// project entities. Field names and types are derived from ProjectService.Get()
    /// (which returns SELECT * from project) and cross-references in TaskService
    /// (projectRecord["abbr"], projectRecord["owner_id"]) and ReportService
    /// (project["account_id"], project["name"]).
    /// </summary>
    public class ProjectRecordBuilder
    {
        private Guid _id = Guid.NewGuid();
        private string _name = "Default Project";
        private string _abbr = "DP";
        private Guid? _ownerId = SystemIds.SystemUserId;
        private Guid? _accountId = null;
        private bool _isBillable = true;
        private Guid _createdBy = SystemIds.SystemUserId;
        private DateTime _createdOn = DateTime.UtcNow;

        /// <summary>Sets the unique identifier for the project record.</summary>
        /// <param name="id">A <see cref="Guid"/> representing the project ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithId(Guid id) { _id = id; return this; }

        /// <summary>Sets the display name of the project.</summary>
        /// <param name="name">The project name string.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithName(string name) { _name = name; return this; }

        /// <summary>
        /// Sets the abbreviation code for the project.
        /// In the monolith, this is used by TaskService.SetCalculationFields() to
        /// generate task keys: projectAbbr + "-" + number.
        /// </summary>
        /// <param name="abbr">The project abbreviation string (e.g., "PRJ", "DP").</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithAbbr(string abbr) { _abbr = abbr; return this; }

        /// <summary>
        /// Sets the owner user ID for the project.
        /// In the monolith, TaskService reads this as (Guid?)projectRecord["owner_id"].
        /// Default is SystemIds.SystemUserId.
        /// </summary>
        /// <param name="ownerId">A nullable <see cref="Guid"/> for the project owner.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithOwnerId(Guid? ownerId) { _ownerId = ownerId; return this; }

        /// <summary>
        /// Sets the account entity reference ID for the project.
        /// In the monolith, ReportService reads this as (Guid)project["account_id"]
        /// for filtering timelogs by account.
        /// </summary>
        /// <param name="accountId">A nullable <see cref="Guid"/> for the account ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithAccountId(Guid? accountId) { _accountId = accountId; return this; }

        /// <summary>
        /// Sets whether the project is billable.
        /// In the monolith, this is referenced via EQL relation
        /// $project_nn_task.is_billable in task queries.
        /// </summary>
        /// <param name="isBillable">True if the project is billable; false otherwise.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithIsBillable(bool isBillable) { _isBillable = isBillable; return this; }

        /// <summary>Sets the ID of the user who created the project.</summary>
        /// <param name="createdBy">A <see cref="Guid"/> for the creator user ID.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithCreatedBy(Guid createdBy) { _createdBy = createdBy; return this; }

        /// <summary>Sets the creation timestamp of the project.</summary>
        /// <param name="createdOn">A <see cref="DateTime"/> for the creation time.</param>
        /// <returns>This builder instance for fluent chaining.</returns>
        public ProjectRecordBuilder WithCreatedOn(DateTime createdOn) { _createdOn = createdOn; return this; }

        /// <summary>
        /// Builds and returns an <see cref="EntityRecord"/> populated with all configured
        /// project fields. All fields match the structure returned by
        /// ProjectService.Get() via EQL "SELECT * from project".
        /// </summary>
        /// <returns>A fully populated <see cref="EntityRecord"/> representing a project.</returns>
        public EntityRecord Build()
        {
            var record = new EntityRecord();
            record["id"] = _id;
            record["name"] = _name;
            record["abbr"] = _abbr;
            record["owner_id"] = _ownerId;
            record["account_id"] = _accountId;
            record["is_billable"] = _isBillable;
            record["created_by"] = _createdBy;
            record["created_on"] = _createdOn;
            return record;
        }
    }
}
