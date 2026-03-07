using System;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Project.Domain.Models
{
	/// <summary>
	/// Defines filter categories for task queue queries based on due date/time status.
	/// Used by TaskService.GetTaskQueue() to construct EQL WHERE clauses for filtering tasks.
	/// Each value is decorated with a SelectOptionAttribute providing a display label for UI rendering.
	/// </summary>
	public enum TasksDueType
	{
		[SelectOption(Label = "all")]
		All = 0,
		[SelectOption(Label = "end time overdue")]
		EndTimeOverdue = 1,
		[SelectOption(Label = "end time due today")]
		EndTimeDueToday = 2,
		[SelectOption(Label = "end time not due")]
		EndTimeNotDue = 3,
		[SelectOption(Label = "start time due")]
		StartTimeDue = 4,
		[SelectOption(Label = "start time not due")]
		StartTimeNotDue = 5,
	}
}
