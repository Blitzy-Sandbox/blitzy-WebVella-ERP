using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Project.Domain.Services;

namespace WebVella.Erp.Service.Project.Jobs
{
	/// <summary>
	/// Daily background job that activates tasks when their <c>start_time</c> date is reached.
	///
	/// Transformed from the monolith's <c>WebVella.Erp.Plugins.Project.Jobs.StartTasksOnStartDate</c>
	/// (32 lines) which was an <c>ErpJob</c>-based scheduled job with attribute
	/// <c>[Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", "Start tasks on start_date", true, JobPriority.Low)]</c>.
	///
	/// Key adaptations for the microservice architecture:
	/// <list type="number">
	///   <item><c>ErpJob</c> base class replaced with <see cref="BackgroundService"/> (IHostedService)</item>
	///   <item><c>[Job(...)]</c> attribute removed — scheduling handled by timer-based <see cref="ExecuteAsync"/> loop</item>
	///   <item><c>Execute(JobContext)</c> replaced with <c>ExecuteAsync(CancellationToken)</c></item>
	///   <item><c>new TaskService()</c> replaced with DI-injected <see cref="TaskService"/> via <see cref="IServiceScopeFactory"/></item>
	///   <item><c>new RecordManager()</c> replaced with DI-injected <see cref="RecordManager"/> via <see cref="IServiceScopeFactory"/></item>
	///   <item>Monolith's <c>ScheduleManager.Current.CreateSchedulePlan(...)</c> replaced with <see cref="CalculateNextRunDelay"/> computing daily 00:10 UTC</item>
	///   <item>Exception handling via <see cref="ILogger{T}"/> instead of ErpJob history table logging</item>
	/// </list>
	///
	/// <para><b>Schedule:</b> Runs once daily at 00:10 UTC every day of the week, matching
	/// the monolith's SchedulePlan configuration (SchedulePlanType.Daily, IntervalInMinutes=1440,
	/// StartDate=00:10:00 UTC, all 7 days enabled).</para>
	///
	/// <para><b>Business Logic (preserved exactly from source):</b></para>
	/// <list type="bullet">
	///   <item>Opens <see cref="SecurityContext.OpenSystemScope()"/> for elevated permissions</item>
	///   <item>Queries tasks via <see cref="TaskService.GetTasksThatNeedStarting()"/>
	///         (status_id = f3fdd750-..., start_time &lt;= DateTime.Now.Date)</item>
	///   <item>Updates each task's status_id to <c>20d73f63-3501-4565-a55e-2d291549a9bd</c> (started/in-progress)</item>
	///   <item>Throws on first update failure (fail-fast within SecurityContext scope, preserving monolith behavior)</item>
	/// </list>
	///
	/// <para><b>Registration:</b> Conditionally registered in Program.cs via
	/// <c>builder.Services.AddHostedService&lt;StartTasksOnStartDateJob&gt;()</c>
	/// when <c>Jobs:Enabled</c> configuration is true.</para>
	/// </summary>
	public class StartTasksOnStartDateJob : BackgroundService
	{
		/// <summary>
		/// Original monolith schedule plan ID preserved for traceability.
		/// From ProjectPlugin.cs: <c>new Guid("6765D758-FB63-478F-B714-5B153AB9A758")</c>.
		/// </summary>
		private static readonly Guid SchedulePlanId = new Guid("6765D758-FB63-478F-B714-5B153AB9A758");

		/// <summary>
		/// Original monolith job type ID preserved for traceability.
		/// From <c>[Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", ...)]</c> attribute.
		/// </summary>
		private static readonly Guid JobTypeId = new Guid("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4");

		/// <summary>
		/// Task status ID that indicates "started/in progress" — hard-coded in original monolith
		/// at source line 23: <c>new Guid("20d73f63-3501-4565-a55e-2d291549a9bd")</c>.
		/// </summary>
		private static readonly Guid StartedStatusId = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");

		private readonly IServiceScopeFactory _serviceScopeFactory;
		private readonly ILogger<StartTasksOnStartDateJob> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="StartTasksOnStartDateJob"/> class.
		/// Uses <see cref="IServiceScopeFactory"/> to create DI scopes at runtime because
		/// <see cref="BackgroundService"/> is registered as a singleton via
		/// <c>AddHostedService&lt;T&gt;()</c>, while <see cref="TaskService"/> and
		/// <see cref="RecordManager"/> are scoped services.
		/// </summary>
		/// <param name="serviceScopeFactory">Factory for creating scoped service containers
		/// to resolve <see cref="TaskService"/> and <see cref="RecordManager"/>.</param>
		/// <param name="logger">Structured logger for job lifecycle events, replacing the
		/// monolith's ErpJob implicit logging to the job history table.</param>
		public StartTasksOnStartDateJob(
			IServiceScopeFactory serviceScopeFactory,
			ILogger<StartTasksOnStartDateJob> logger)
		{
			_serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Main execution loop implementing daily scheduling at 00:10 UTC.
		/// Replaces the monolith's <c>ErpJob.Execute(JobContext)</c> with a continuous
		/// timer-based loop that calculates the delay until the next 00:10 UTC run,
		/// waits, then executes <see cref="RunJobAsync"/>.
		///
		/// Preserves the monolith schedule: SchedulePlanType.Daily, all 7 days,
		/// StartDate = 00:10:00 UTC, IntervalInMinutes = 1440 (once every 24 hours).
		/// </summary>
		/// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
		/// <returns>A Task that completes when the job is stopped.</returns>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("StartTasksOnStartDateJob started. Schedule: daily at 00:10 UTC.");

			while (!stoppingToken.IsCancellationRequested)
			{
				var delay = CalculateNextRunDelay();
				_logger.LogInformation("Next StartTasksOnStartDateJob run in {Delay}", delay);

				try
				{
					await Task.Delay(delay, stoppingToken);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				await RunJobAsync(stoppingToken);
			}

			_logger.LogInformation("StartTasksOnStartDateJob stopped.");
		}

		/// <summary>
		/// Calculates the <see cref="TimeSpan"/> until the next 00:10 UTC run.
		///
		/// Translated from the monolith's SchedulePlan configuration in ProjectPlugin.cs:
		/// <c>StartDate = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, 0, 10, 0, DateTimeKind.Utc)</c>
		/// with <c>IntervalInMinutes = 1440</c> (24 hours) and all 7 days of week enabled.
		///
		/// If today's 00:10 UTC has already passed, the next run is scheduled for tomorrow at 00:10 UTC.
		/// </summary>
		/// <returns>The time remaining until the next scheduled 00:10 UTC execution.</returns>
		private static TimeSpan CalculateNextRunDelay()
		{
			var now = DateTime.UtcNow;
			var todayRun = new DateTime(now.Year, now.Month, now.Day, 0, 10, 0, DateTimeKind.Utc);

			// If today's 00:10 UTC has already passed, schedule for tomorrow
			var nextRun = now < todayRun ? todayRun : todayRun.AddDays(1);

			return nextRun - now;
		}

		/// <summary>
		/// Executes the core task activation business logic.
		///
		/// Preserved exactly from monolith source lines 14-30:
		/// <list type="number">
		///   <item>Opens <see cref="SecurityContext.OpenSystemScope()"/> for elevated permissions
		///         (source line 16)</item>
		///   <item>Retrieves tasks needing activation via <see cref="TaskService.GetTasksThatNeedStarting()"/>
		///         (source line 18, was <c>new TaskService().GetTasksThatNeedStarting()</c>)</item>
		///   <item>For each task, constructs a patch <see cref="EntityRecord"/> with id and status_id
		///         (source lines 21-23)</item>
		///   <item>Updates each task via <see cref="RecordManager.UpdateRecord(string, EntityRecord)"/>
		///         (source line 24, was <c>new RecordManager().UpdateRecord(...)</c>)</item>
		///   <item>Throws on first failure: <c>throw new Exception(updateResult.Message)</c>
		///         (source lines 25-27) — preserves monolith fail-fast behavior within the scope</item>
		/// </list>
		///
		/// Error handling strategy:
		/// - <see cref="OperationCanceledException"/>: logged as warning and rethrown for graceful shutdown
		/// - All other exceptions: logged as error but NOT rethrown, allowing the next scheduled run
		/// - Individual task update failures throw within the SecurityContext scope (monolith behavior preserved),
		///   which means remaining tasks in the current run are skipped on failure
		/// </summary>
		/// <param name="stoppingToken">Cancellation token checked between task iterations for graceful shutdown.</param>
		/// <returns>A Task representing the asynchronous job execution.</returns>
		private async Task RunJobAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("StartTasksOnStartDateJob executing at {Time}", DateTime.UtcNow);

			try
			{
				using (var scope = _serviceScopeFactory.CreateScope())
				{
					var taskService = scope.ServiceProvider.GetRequiredService<TaskService>();

					using (SecurityContext.OpenSystemScope())
					{
						var tasks = taskService.GetTasksThatNeedStarting();

						_logger.LogInformation("Found {Count} tasks that need starting.", tasks.Count);

						foreach (var task in tasks)
						{
							stoppingToken.ThrowIfCancellationRequested();

							var patchRecord = new EntityRecord();
							patchRecord["id"] = (Guid)task["id"];
							patchRecord["status_id"] = StartedStatusId;

							var recordManager = scope.ServiceProvider.GetRequiredService<RecordManager>();
							var updateResult = recordManager.UpdateRecord("task", patchRecord);
							if (!updateResult.Success)
							{
								throw new Exception(updateResult.Message);
							}
						}
					}
				}

				_logger.LogInformation("StartTasksOnStartDateJob completed successfully.");
			}
			catch (OperationCanceledException)
			{
				_logger.LogWarning("StartTasksOnStartDateJob was cancelled.");
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "StartTasksOnStartDateJob failed.");
				// Do not rethrow — allow the job to retry on next schedule
			}

			await Task.CompletedTask;
		}
	}
}
