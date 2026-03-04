using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Admin.Services;

namespace WebVella.Erp.Service.Admin.Jobs
{
	/// <summary>
	/// Background service that periodically purges accumulated job execution logs and error logs.
	/// Migrated from WebVella.Erp.Plugins.SDK.Jobs.ClearJobAndErrorLogsJob.
	///
	/// Original monolith registration:
	///   [Job("99D9A8BB-31E6-4436-B0C2-20BD6AA23786", "Clear job and error logs job", true, JobPriority.Medium)]
	///
	/// Original schedule (from SdkPlugin.cs lines 72-103):
	///   Schedule Plan ID : 8CC1DF20-0967-4635-B44A-45FD90819105
	///   Name             : "Clear job and error logs."
	///   Type             : SchedulePlanType.Daily
	///   Start Date       : Daily at 00:00:02 UTC
	///   Scheduled Days   : All days enabled (Mon-Sun)
	///   Interval         : 1440 minutes (24 hours)
	///   StartTimespan    : 0
	///   EndTimespan      : 1440
	///   Job Type ID      : 99D9A8BB-31E6-4436-B0C2-20BD6AA23786
	///   Enabled          : true
	///
	/// In the microservice architecture this translates to a <see cref="BackgroundService"/> with
	/// a configurable timer interval (default 1440 minutes = 24 hours), read from
	/// <c>appsettings.json → Jobs:ClearLogsIntervalMinutes</c>.
	///
	/// DI registration in Program.cs:
	///   <c>builder.Services.AddHostedService&lt;ClearJobAndErrorLogsJob&gt;();</c>
	///
	/// Required DI registrations:
	///   - <see cref="IServiceScopeFactory"/> (built-in)
	///   - <see cref="ILogger{ClearJobAndErrorLogsJob}"/> (built-in)
	///   - <see cref="IConfiguration"/> (built-in)
	///   - <see cref="ILogService"/> registered as <c>builder.Services.AddScoped&lt;ILogService, LogService&gt;();</c>
	///   - <see cref="SecurityContext"/> from SharedKernel (static, no registration needed)
	/// </summary>
	public class ClearJobAndErrorLogsJob : BackgroundService
	{
		private readonly IServiceScopeFactory _serviceScopeFactory;
		private readonly ILogger<ClearJobAndErrorLogsJob> _logger;
		private readonly TimeSpan _interval;

		/// <summary>
		/// Initializes a new instance of <see cref="ClearJobAndErrorLogsJob"/> with DI-injected dependencies.
		/// </summary>
		/// <param name="serviceScopeFactory">
		/// Factory for creating DI scopes inside the singleton BackgroundService,
		/// enabling resolution of scoped services like <see cref="ILogService"/>.
		/// </param>
		/// <param name="logger">
		/// Structured logger replacing the monolith's <c>new Log().Create(LogType.Error, ...)</c> pattern.
		/// </param>
		/// <param name="configuration">
		/// Application configuration for reading <c>Jobs:ClearLogsIntervalMinutes</c>.
		/// Replaces the monolith's <c>SchedulePlan.IntervalInMinutes = 1440</c> static configuration.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="serviceScopeFactory"/> or <paramref name="logger"/> is null.
		/// </exception>
		public ClearJobAndErrorLogsJob(
			IServiceScopeFactory serviceScopeFactory,
			ILogger<ClearJobAndErrorLogsJob> logger,
			IConfiguration configuration)
		{
			_serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));

			// Default: run every 24 hours (1440 minutes), matching the original SchedulePlan.IntervalInMinutes = 1440
			// Configurable via appsettings.json -> Jobs:ClearLogsIntervalMinutes
			var intervalMinutes = configuration.GetValue<int>("Jobs:ClearLogsIntervalMinutes", 1440);
			_interval = TimeSpan.FromMinutes(intervalMinutes);
		}

		/// <summary>
		/// Executes the background timer loop that invokes <see cref="RunCleanupAsync"/>
		/// at the configured interval. Replaces the monolith's <c>ScheduleManager</c> +
		/// <c>ErpJob.Execute(JobContext)</c> pattern with ASP.NET Core's hosted service lifecycle.
		/// </summary>
		/// <param name="stoppingToken">
		/// Cancellation token signaled during graceful application shutdown.
		/// Replaces the monolith's <c>JobContext</c> parameter.
		/// </param>
		/// <returns>A task that completes when the service is stopped.</returns>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			// Initial delay to allow other services to start first.
			// Similar to the monolith's ScheduleManager non-DEBUG 120-second startup delay
			// and the original schedule start time offset of 00:00:02 UTC.
			await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

			while (!stoppingToken.IsCancellationRequested)
			{
				await RunCleanupAsync(stoppingToken);

				try
				{
					await Task.Delay(_interval, stoppingToken);
				}
				catch (OperationCanceledException)
				{
					// Graceful shutdown — exit the loop
					break;
				}
			}
		}

		/// <summary>
		/// Performs a single cleanup pass by delegating to <see cref="ILogService.ClearJobAndErrorLogs"/>.
		/// Preserves the exact business rule pattern from the monolith source (lines 12-26):
		///   1. Opens a system-level security scope via <see cref="SecurityContext.OpenSystemScope"/>
		///   2. Resolves <see cref="ILogService"/> from a DI scope (replacing <c>new LogService()</c>)
		///   3. Invokes <see cref="ILogService.ClearJobAndErrorLogs"/>
		///   4. Catches and logs any errors without propagation (error isolation)
		///
		/// The actual cleanup algorithm (30-day retention, 1000-record threshold for system_log and
		/// jobs tables, status filters 3/4/5/6 for completed/canceled/failed/aborted jobs) lives
		/// entirely in <see cref="ILogService"/> and is NOT duplicated here.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token for cooperative shutdown.</param>
		private async Task RunCleanupAsync(CancellationToken stoppingToken)
		{
			// CRITICAL: Preserve the SecurityContext.OpenSystemScope() pattern from source line 14.
			// This ensures the operation runs under system-level permissions (Administrator role,
			// bypasses all permission checks) regardless of invoking context.
			// The using block ensures proper IDisposable scope disposal on both normal
			// completion and exceptional exit (matching source pattern exactly).
			using (SecurityContext.OpenSystemScope())
			{
				try
				{
					// CRITICAL: Use DI scope instead of direct instantiation.
					// Source line 18 had: new LogService().ClearJobAndErrorLogs();
					// Now resolved via IServiceScopeFactory to get scoped ILogService from DI,
					// ensuring proper lifetime management and constructor dependency injection.
					using var scope = _serviceScopeFactory.CreateScope();
					var logService = scope.ServiceProvider.GetRequiredService<ILogService>();

					// Delegate to LogService.ClearJobAndErrorLogs() — preserves exact same
					// behavior as source line 18. Run synchronous method on thread pool thread
					// to avoid blocking the BackgroundService execution thread.
					await Task.Run(() => logService.ClearJobAndErrorLogs(), stoppingToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					// CRITICAL: Preserve error handling pattern from source (lines 20-23).
					// Original: new Log().Create(LogType.Error, "ClearJobAndErrorLogsJob", ex);
					// Converted to ILogger — diagnostics subsystem is now standard ASP.NET Core
					// logging. This prevents exceptions from escaping the job boundary and
					// destabilizing the scheduler loop.
					_logger.LogError(ex, "Error occurred while executing ClearJobAndErrorLogsJob");
				}
			}
		}
	}
}
