using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Mail.Domain.Services;

namespace WebVella.Erp.Service.Mail.Jobs
{
	/// <summary>
	/// Strongly-typed configuration for mail queue background job behaviour.
	/// Bound from the <c>Jobs</c> section of <c>appsettings.json</c>.
	///
	/// Example configuration:
	/// <code>
	/// {
	///   "Jobs": {
	///     "Enabled": true,
	///     "QueueProcessingIntervalMinutes": 10
	///   }
	/// }
	/// </code>
	///
	/// Defaults preserve the monolith's MailPlugin schedule plan interval of 10 minutes
	/// (MailPlugin.cs line 69: <c>IntervalInMinutes = 10</c>).
	/// </summary>
	public class JobSettings
	{
		/// <summary>
		/// Controls whether the mail queue processing job is active.
		/// When false, the background service starts but immediately exits without processing.
		/// Default is true, matching the monolith's <c>SchedulePlan.Enabled = true</c>.
		/// </summary>
		public bool Enabled { get; set; } = true;

		/// <summary>
		/// Interval in minutes between successive queue processing cycles.
		/// Default is 10 minutes, preserving the monolith's MailPlugin schedule plan
		/// (<c>IntervalInMinutes = 10</c>, running 24/7 on all days of the week).
		/// Minimum effective value is 1 minute.
		/// </summary>
		public int QueueProcessingIntervalMinutes { get; set; } = 10;
	}

	/// <summary>
	/// Timed background service that processes the SMTP outbound email queue
	/// for the Mail microservice.
	///
	/// Replaces the monolith's <c>ProcessSmtpQueueJob : ErpJob</c> which used the
	/// <c>[Job("9b301dca-6c81-40dd-887c-efd31c23bd77", ...)]</c> attribute pattern
	/// with a modern ASP.NET Core <see cref="BackgroundService"/> (IHostedService) pattern.
	///
	/// Architecture:
	///   - This job is intentionally thin — an adapter/bridge between the ASP.NET Core
	///     hosting framework and the <see cref="SmtpService"/> domain service.
	///   - Each timer tick creates a new DI scope, resolves <see cref="SmtpService"/>,
	///     opens a system security scope, and delegates to <c>ProcessSmtpQueue()</c>.
	///   - All SMTP business logic (EQL queries, batch processing, retry, abort logic)
	///     is encapsulated in <see cref="SmtpService.ProcessSmtpQueue"/>.
	///
	/// Monolith equivalents:
	///   - Job class: <c>WebVella.Erp.Plugins.Mail.Jobs.ProcessSmtpQueueJob</c>
	///   - Schedule plan: MailPlugin.SetSchedulePlans() — 10-minute interval, 24/7, all days
	///   - Job GUID: 9b301dca-6c81-40dd-887c-efd31c23bd77
	///   - Schedule GUID: 8f410aca-a537-4c3f-b49b-927670534c07
	///
	/// Registration: Added as <c>builder.Services.AddHostedService&lt;ProcessMailQueueJob&gt;()</c>
	/// in Program.cs. Configuration bound from <c>appsettings.json → Jobs</c> section.
	/// </summary>
	public class ProcessMailQueueJob : BackgroundService
	{
		/// <summary>
		/// Original Job GUID from the monolith's <c>[Job]</c> attribute on <c>ProcessSmtpQueueJob</c>.
		/// Value: 9b301dca-6c81-40dd-887c-efd31c23bd77.
		/// Preserved for migration reference and potential data migration compatibility
		/// with the monolith's <c>jobs</c> table records.
		/// </summary>
		public static readonly Guid OriginalJobId = new Guid("9b301dca-6c81-40dd-887c-efd31c23bd77");

		/// <summary>
		/// Original Schedule Plan GUID from the monolith's <c>MailPlugin.SetSchedulePlans()</c>.
		/// Value: 8f410aca-a537-4c3f-b49b-927670534c07.
		/// Preserved for migration reference and potential data migration compatibility
		/// with the monolith's <c>schedule_plan</c> table records.
		/// </summary>
		public static readonly Guid OriginalSchedulePlanId = new Guid("8f410aca-a537-4c3f-b49b-927670534c07");

		/// <summary>
		/// Static lock object for the concurrency guard. Prevents concurrent queue
		/// processing if the timer fires again before the previous cycle completes.
		/// Mirrors the static lock pattern from SmtpInternalService (lines 28-29).
		/// </summary>
		private static readonly object _lockObject = new object();

		/// <summary>
		/// Static flag indicating whether queue processing is currently in progress.
		/// Checked under <see cref="_lockObject"/> to prevent overlapping executions.
		/// Mirrors the static guard from SmtpInternalService (lines 28-29, 831-837).
		/// </summary>
		private static bool _queueProcessingInProgress = false;

		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<ProcessMailQueueJob> _logger;
		private readonly JobSettings _jobSettings;

		/// <summary>
		/// Constructs the mail queue processing background job with required dependencies.
		/// </summary>
		/// <param name="scopeFactory">
		/// Factory for creating DI service scopes. Each queue processing cycle creates
		/// a new scope to resolve a fresh <see cref="SmtpService"/> instance, replacing
		/// the monolith's <c>new SmtpInternalService()</c> direct instantiation pattern.
		/// </param>
		/// <param name="logger">
		/// Structured logger for recording job lifecycle events, processing errors,
		/// and configuration status.
		/// </param>
		/// <param name="jobSettings">
		/// Strongly-typed options bound from <c>appsettings.json → Jobs</c> section.
		/// Controls the <c>Enabled</c> flag and <c>QueueProcessingIntervalMinutes</c>
		/// timer interval (default 10 minutes).
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="scopeFactory"/>, <paramref name="logger"/>,
		/// or <paramref name="jobSettings"/> is null.
		/// </exception>
		public ProcessMailQueueJob(
			IServiceScopeFactory scopeFactory,
			ILogger<ProcessMailQueueJob> logger,
			IOptions<JobSettings> jobSettings)
		{
			_scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));

			if (jobSettings == null)
				throw new ArgumentNullException(nameof(jobSettings));

			_jobSettings = jobSettings.Value ?? throw new ArgumentNullException(nameof(jobSettings), "JobSettings.Value must not be null.");
		}

		/// <summary>
		/// Long-running execution method called by the ASP.NET Core hosting framework.
		/// Implements the timer loop pattern that replaces the monolith's
		/// <c>ScheduleManager</c> + <c>JobPool</c> execution model.
		///
		/// Behaviour:
		///   1. If <c>Jobs:Enabled</c> is false, logs a warning and exits immediately.
		///   2. Enters a <c>while</c> loop that runs until the host signals shutdown
		///      via <paramref name="stoppingToken"/>.
		///   3. Each iteration calls <see cref="ProcessQueueAsync"/> to process pending
		///      emails, then waits for the configured interval before the next cycle.
		///   4. Exceptions during processing are caught, logged, and do not stop the loop.
		///   5. <see cref="OperationCanceledException"/> from <c>Task.Delay</c> during
		///      shutdown is caught and terminates the loop gracefully.
		/// </summary>
		/// <param name="stoppingToken">
		/// Cancellation token triggered when the host is shutting down.
		/// Propagated to <c>Task.Delay</c> for immediate cancellation during shutdown.
		/// </param>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_jobSettings.Enabled)
			{
				_logger.LogWarning(
					"ProcessMailQueueJob is disabled via configuration (Jobs:Enabled=false). " +
					"No mail queue processing will occur. Original monolith job ID: {OriginalJobId}",
					OriginalJobId);
				return;
			}

			// Ensure the interval is at least 1 minute to prevent tight-loop resource exhaustion
			int intervalMinutes = Math.Max(1, _jobSettings.QueueProcessingIntervalMinutes);

			_logger.LogInformation(
				"ProcessMailQueueJob started. Queue processing interval: {IntervalMinutes} minutes. " +
				"Original monolith job ID: {OriginalJobId}, schedule plan ID: {OriginalSchedulePlanId}",
				intervalMinutes,
				OriginalJobId,
				OriginalSchedulePlanId);

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await ProcessQueueAsync(stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					// Host is shutting down — exit the loop gracefully
					_logger.LogInformation("ProcessMailQueueJob received cancellation signal during queue processing. Stopping.");
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(
						ex,
						"Error occurred during mail queue processing cycle. " +
						"The job will retry after {IntervalMinutes} minutes.",
						intervalMinutes);
				}

				try
				{
					await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					// Host is shutting down during the delay — exit gracefully
					_logger.LogInformation("ProcessMailQueueJob received cancellation signal during delay. Stopping.");
					break;
				}
			}

			_logger.LogInformation("ProcessMailQueueJob has stopped.");
		}

		/// <summary>
		/// Processes the SMTP email queue in a single cycle, preserving the exact execution
		/// pattern from the monolith's <c>ProcessSmtpQueueJob.Execute()</c> method:
		///
		///   1. Acquire static concurrency guard (prevents overlapping cycles).
		///   2. Create a new DI scope and resolve <see cref="SmtpService"/>.
		///   3. Open system security scope via <see cref="SecurityContext.OpenSystemScope()"/>.
		///   4. Delegate to <see cref="SmtpService.ProcessSmtpQueue()"/>.
		///   5. Release concurrency guard in <c>finally</c> block.
		///
		/// The static lock/guard pattern mirrors SmtpInternalService (lines 28-29, 831-837)
		/// and provides defense-in-depth concurrency protection at the job level.
		/// <see cref="SmtpService.ProcessSmtpQueue()"/> also has its own internal lock guard
		/// for cases where it may be called from other entry points.
		/// </summary>
		/// <param name="cancellationToken">
		/// Cancellation token for cooperative shutdown. Not currently propagated to
		/// <c>SmtpService.ProcessSmtpQueue()</c> as the method is synchronous (preserving
		/// the monolith's synchronous processing model).
		/// </param>
		private async Task ProcessQueueAsync(CancellationToken cancellationToken)
		{
			// Concurrency guard: prevent overlapping queue processing cycles.
			// Preserves the exact static lock/guard pattern from SmtpInternalService
			// (lines 28-29, 831-837 of the monolith source).
			lock (_lockObject)
			{
				if (_queueProcessingInProgress)
				{
					_logger.LogInformation(
						"ProcessMailQueueJob: Queue processing already in progress. Skipping this cycle.");
					return;
				}

				_queueProcessingInProgress = true;
			}

			try
			{
				// Create a new DI scope for this processing cycle.
				// SmtpService is registered as a scoped service; each cycle gets a fresh instance.
				// Replaces the monolith's direct instantiation: new SmtpInternalService()
				using var scope = _scopeFactory.CreateScope();
				var smtpService = scope.ServiceProvider.GetRequiredService<SmtpService>();

				// Open system security scope for background job execution.
				// Preserves the exact pattern from ProcessSmtpQueueJob.Execute() (line 12):
				//   using (SecurityContext.OpenSystemScope())
				// This ensures the job runs under system-level security context without
				// user authentication, granting unlimited permissions per AAP 0.8.3.
				using (SecurityContext.OpenSystemScope())
				{
					// Delegate to SmtpService.ProcessSmtpQueue() which contains ALL the
					// business logic: EQL query for pending emails (batch size 10),
					// do-while loop, SMTP send with retry, abort on missing service,
					// and configurable max retries/wait minutes from SMTP service settings.
					// The method is synchronous, preserving the monolith's execution model.
					await Task.Run(() => smtpService.ProcessSmtpQueue(), cancellationToken);
				}
			}
			finally
			{
				// Release the concurrency guard, allowing the next timer tick to process.
				// Mirrors SmtpInternalService finally block (lines 873-877).
				lock (_lockObject)
				{
					_queueProcessingInProgress = false;
				}
			}
		}
	}
}
