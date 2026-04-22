using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Defines task due-date filter modes for task queue and list filtering.
    /// Each value corresponds to a specific date-based filter condition applied
    /// against task <c>start_time</c> and <c>end_time</c> fields relative to the
    /// current date boundaries (<c>currentDateStart</c> = start of today,
    /// <c>currentDateEnd</c> = end of today).
    /// </summary>
    /// <remarks>
    /// Ported from <c>WebVella.Erp.Plugins.Project.Model.TasksDueType</c>.
    /// The monolith's <c>[SelectOption(Label = "...")]</c> attributes have been removed;
    /// label metadata is now handled by the React SPA frontend.
    /// Decorated with <see cref="JsonStringEnumConverter"/> for AOT-friendly JSON
    /// serialization of enum names (e.g., <c>"EndTimeOverdue"</c> instead of <c>1</c>).
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<TasksDueType>))]
    public enum TasksDueType
    {
        /// <summary>
        /// No filtering applied — all tasks are returned regardless of their
        /// start or end time values.
        /// </summary>
        All = 0,

        /// <summary>
        /// Tasks whose end time is past due: <c>end_time &lt; currentDateStart</c>.
        /// Selects tasks that should have been completed before today.
        /// </summary>
        EndTimeOverdue = 1,

        /// <summary>
        /// Tasks due today: <c>end_time &gt;= currentDateStart AND end_time &lt; currentDateEnd</c>.
        /// Selects tasks whose end time falls within the current day boundaries.
        /// </summary>
        EndTimeDueToday = 2,

        /// <summary>
        /// Tasks not yet due: <c>end_time &gt;= currentDateEnd OR end_time IS NULL</c>.
        /// Selects tasks whose end time is in the future or has not been set.
        /// </summary>
        EndTimeNotDue = 3,

        /// <summary>
        /// Tasks ready to start: <c>start_time &lt; currentDateEnd OR start_time IS NULL</c>.
        /// Selects tasks whose start time has already passed or has not been set,
        /// indicating they are eligible to begin work.
        /// </summary>
        StartTimeDue = 4,

        /// <summary>
        /// Tasks not yet ready to start: <c>start_time &gt; currentDateEnd</c>.
        /// Selects tasks whose start time is in the future, indicating they should
        /// not yet be worked on.
        /// </summary>
        StartTimeNotDue = 5
    }
}
