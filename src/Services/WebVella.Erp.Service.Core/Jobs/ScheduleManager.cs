using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Schedule plan orchestrator for the Core Platform microservice.
	/// Manages schedule plan CRUD, computes NextTriggerTime for
	/// Interval/Daily/Weekly/Monthly plans, runs a polling loop to trigger
	/// ready plans, and creates jobs via <see cref="JobManager"/>.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Jobs.ScheduleManager</c>
	/// (SheduleManager.cs, 724 lines — spelling corrected) with the following
	/// architectural changes:
	/// - Static singleton pattern (<c>ScheduleManager.Current</c>) removed;
	///   registered as singleton in DI.
	/// - Inherits <see cref="BackgroundService"/> to merge the monolith's separate
	///   <c>ErpJobScheduleService</c> into this class (eliminating the
	///   polling-for-Current pattern from ErpBackgroundServices.cs lines 7-22).
	/// - <c>JobManagerSettings</c> replaced with <see cref="IConfiguration"/> injection.
	/// - <c>WebVella.Erp.Diagnostics.Log</c> replaced with <see cref="ILogger{T}"/>.
	/// - <c>JobManager.Current.CreateJob()</c> replaced with DI-resolved
	///   <see cref="JobManager"/> via <see cref="IServiceProvider"/>.
	/// - <c>DbContext.CreateContext/CloseContext</c> error handling replaced with
	///   structured logging via ILogger.
	/// - All original business logic (CRUD, trigger calculations, processing loops)
	///   preserved exactly.
	/// </summary>
	public class ScheduleManager : BackgroundService
	{
		#region <--- Private Fields --->

		/// <summary>
		/// Job data persistence service using direct PostgreSQL access.
		/// Created from configuration connection string in the constructor.
		/// Replaces the monolith's instance <c>JobService</c> property.
		/// </summary>
		private readonly JobDataService JobService;

		/// <summary>
		/// Structured logger replacing the monolith's
		/// <c>WebVella.Erp.Diagnostics.Log</c> class (which persisted error
		/// logs to the database via DbContext).
		/// </summary>
		private readonly ILogger<ScheduleManager> _logger;

		/// <summary>
		/// Service provider for resolving <see cref="JobManager"/> at runtime.
		/// Replaces the monolith's static <c>JobManager.Current</c> access.
		/// </summary>
		private readonly IServiceProvider _serviceProvider;

		/// <summary>
		/// Whether the job/schedule system is enabled.
		/// Read from <c>Jobs:Enabled</c> configuration (default false).
		/// Replaces the monolith's <c>Settings.Enabled</c>.
		/// </summary>
		private readonly bool _enabled;

		/// <summary>
		/// Startup delay in seconds before the schedule processing loop begins.
		/// Read from <c>Jobs:StartupDelaySeconds</c> configuration (default 120).
		/// Replaces the monolith's <c>#if !DEBUG Task.Delay(120000)</c> pattern.
		/// </summary>
		private readonly int _startupDelaySeconds;

		#endregion

		#region <--- Constructor --->

		/// <summary>
		/// Initializes a new instance of the <see cref="ScheduleManager"/> class
		/// with dependency injection. Replaces the monolith's private constructors
		/// and static <c>Initialize()</c> method.
		/// </summary>
		/// <param name="configuration">Application configuration for reading
		/// connection strings and job settings.</param>
		/// <param name="logger">Structured logger for error and diagnostic
		/// logging.</param>
		/// <param name="serviceProvider">Service provider for resolving
		/// <see cref="JobManager"/> at runtime.</param>
		public ScheduleManager(
			IConfiguration configuration,
			ILogger<ScheduleManager> logger,
			IServiceProvider serviceProvider)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

			// Read per-service configuration (replacing monolith's static
			// JobManagerSettings and ErpSettings patterns)
			_enabled = configuration.GetValue<bool>("Jobs:Enabled", false);
			_startupDelaySeconds = configuration.GetValue<int>("Jobs:StartupDelaySeconds", 120);
			if (_startupDelaySeconds < 0)
				_startupDelaySeconds = 120;

			// Create JobDataService with connection string from configuration
			// (replacing monolith's: new JobDataService(Settings))
			var settings = new JobManagerSettings
			{
				DbConnectionString = configuration.GetConnectionString("Default"),
				Enabled = _enabled
			};
			JobService = new JobDataService(settings);

			_logger.LogInformation(
				"ScheduleManager initialized. Enabled={Enabled}, StartupDelay={StartupDelay}s.",
				_enabled, _startupDelaySeconds);
		}

		#endregion

		#region <--- BackgroundService Overrides --->

		/// <summary>
		/// Entry point for the hosted service. Replaces the monolith's separate
		/// <c>ErpJobScheduleService</c> which polled for
		/// <c>ScheduleManager.Current != null</c> before dispatching.
		///
		/// Applies configurable startup delay, then enters the schedule
		/// processing loop.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token signaling service
		/// shutdown.</param>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_enabled)
				return;

			// Configurable initial delay before starting schedule processing
			// (replacing monolith's #if !DEBUG Task.Delay(120000) pattern)
			try
			{
				await Task.Delay(_startupDelaySeconds * 1000, stoppingToken);
			}
			catch (TaskCanceledException)
			{
				return;
			}

			// Run schedule processing loop
			await ProcessSchedulesLoopAsync(stoppingToken);
		}

		/// <summary>
		/// Graceful shutdown handler. Logs shutdown and delegates to base
		/// implementation for cancellation token signaling.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token for shutdown.</param>
		public override async Task StartAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("ScheduleManager starting...");
			await base.StartAsync(stoppingToken);
		}

		/// <summary>
		/// Graceful shutdown handler. Logs shutdown and delegates to base
		/// implementation.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token for shutdown.</param>
		public override async Task StopAsync(CancellationToken stoppingToken)
		{
			_logger.LogInformation("ScheduleManager stopping...");
			await base.StopAsync(stoppingToken);
		}

		#endregion

		#region <--- Schedule Plan CRUD --->

		/// <summary>
		/// Creates a new schedule plan with auto-generated ID if not set,
		/// computes the next trigger time, and persists to database.
		///
		/// Preserved EXACTLY from monolith source lines 37-44.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan to create.</param>
		/// <returns><c>true</c> if creation succeeded.</returns>
		public bool CreateSchedulePlan(SchedulePlan schedulePlan)
		{
			if (schedulePlan.Id == Guid.Empty)
				schedulePlan.Id = Guid.NewGuid();

			schedulePlan.NextTriggerTime = FindSchedulePlanNextTriggerDate(schedulePlan);

			return JobService.CreateSchedule(schedulePlan);
		}

		/// <summary>
		/// Updates a schedule plan (full update) in the database.
		///
		/// Preserved EXACTLY from monolith source lines 47-50.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan with updated fields.</param>
		/// <returns><c>true</c> if update succeeded.</returns>
		public bool UpdateSchedulePlan(SchedulePlan schedulePlan)
		{
			return JobService.UpdateSchedule(schedulePlan);
		}

		/// <summary>
		/// Updates only the trigger times and last started job ID of a schedule plan.
		/// Used in the processing loop after computing new trigger times.
		///
		/// Preserved EXACTLY from monolith source lines 52-56.
		/// Access changed from private to public per microservice export schema.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan with updated trigger metadata.</param>
		/// <returns><c>true</c> if update succeeded.</returns>
		public bool UpdateSchedulePlanShort(SchedulePlan schedulePlan)
		{
			return JobService.UpdateSchedule(schedulePlan.Id, schedulePlan.LastTriggerTime, schedulePlan.NextTriggerTime,
												schedulePlan.LastModifiedBy, schedulePlan.LastStartedJobId);
		}

		/// <summary>
		/// Retrieves a schedule plan by its unique identifier.
		///
		/// Preserved EXACTLY from monolith source lines 58-61.
		/// </summary>
		/// <param name="id">The schedule plan's unique identifier.</param>
		/// <returns>The <see cref="SchedulePlan"/> instance, or null if not found.</returns>
		public SchedulePlan GetSchedulePlan(Guid id)
		{
			return JobService.GetSchedulePlan(id);
		}

		/// <summary>
		/// Retrieves all schedule plans from the database.
		///
		/// Preserved EXACTLY from monolith source lines 63-66.
		/// </summary>
		/// <returns>List of all <see cref="SchedulePlan"/> instances.</returns>
		public List<SchedulePlan> GetSchedulePlans()
		{
			return JobService.GetSchedulePlans();
		}

		/// <summary>
		/// Triggers a schedule plan immediately by setting its next trigger time
		/// to one minute from now.
		///
		/// Preserved EXACTLY from monolith source lines 68-72.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan to trigger.</param>
		public void TriggerNowSchedulePlan(SchedulePlan schedulePlan)
		{
			schedulePlan.NextTriggerTime = DateTime.UtcNow.AddMinutes(1);
			UpdateSchedulePlanShort(schedulePlan);
		}

		#endregion

		#region <--- Synchronous Processing (Process) --->

		/// <summary>
		/// Synchronous schedule processing loop with Thread.Sleep polling.
		/// Preserved from monolith source lines 79-226 with the following changes:
		/// - <c>Settings.Enabled</c> replaced with <c>_enabled</c>.
		/// - <c>JobManager.Current.CreateJob()</c> replaced with DI-resolved JobManager.
		/// - <c>DbContext.CreateContext/CloseContext</c> + <c>Log</c> replaced
		///   with <c>_logger</c>.
		///
		/// Runs indefinitely with 12-second polling intervals. Used by
		/// fire-and-forget dispatch patterns.
		/// </summary>
		public void Process()
		{
			// Enabled check (preserved from source line 81)
			if (!_enabled)
				return;

			while (true)
			{
				try
				{
					// Get ready for execution schedules
					// (preserved from source line 91)
					List<SchedulePlan> schedulePlans = JobService.GetReadyForExecutionScheduledPlans();

					// foreach schedule if it's time create a job and save it to db
					// (preserved from source lines 94-199)
					foreach (var schedulePlan in schedulePlans)
					{
						// Null guard (preserved from source lines 96-97)
						if (schedulePlan is null || schedulePlan.JobType is null)
							continue;

						// Run new job if last one is finished or canceled
						// (preserved from source lines 100-102)
						bool startNewJob = true;
						if (schedulePlan.LastStartedJobId.HasValue)
							startNewJob = JobService.IsJobFinished(schedulePlan.LastStartedJobId.Value);

						// Calculate next schedule run time and update
						// (preserved from source lines 105-184)
						switch (schedulePlan.Type)
						{
							case SchedulePlanType.Interval:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime startDate = DateTime.UtcNow;

									DateTime? nextActivation = FindIntervalSchedulePlanNextTriggerDate(schedulePlan, startDate, schedulePlan.LastTriggerTime);
									if (nextActivation.HasValue)
									{
										schedulePlan.NextTriggerTime = nextActivation.Value;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Daily:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime startDate = DateTime.UtcNow;

									DateTime? nextActivation = FindDailySchedulePlanNextTriggerDate(schedulePlan, startDate.AddMinutes(1),
											schedulePlan.StartDate.HasValue ? schedulePlan.StartDate.Value : startDate);
									if (nextActivation.HasValue)
									{
										schedulePlan.NextTriggerTime = nextActivation.Value;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Weekly:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime nextActivation = schedulePlan.LastTriggerTime.HasValue
																  ? schedulePlan.LastTriggerTime.Value.AddDays(7)
																  : DateTime.UtcNow.AddDays(7);

									if ((!schedulePlan.EndDate.HasValue) || (nextActivation < schedulePlan.EndDate.Value))
									{
										schedulePlan.NextTriggerTime = nextActivation;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Monthly:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime nextActivation = schedulePlan.LastTriggerTime.HasValue
																  ? schedulePlan.LastTriggerTime.Value.AddMonths(1)
																  : DateTime.UtcNow.AddMonths(1);
									if ((!schedulePlan.EndDate.HasValue) || (nextActivation < schedulePlan.EndDate.Value))
									{
										schedulePlan.NextTriggerTime = nextActivation;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}
									break;
								}
						}

						// Create job if ready (preserved from source lines 186-197)
						if (startNewJob)
						{
							try
							{
								var jobManager = _serviceProvider.GetRequiredService<JobManager>();
								Job job = jobManager.CreateJob(schedulePlan.JobType.Id, schedulePlan.JobAttributes, schedulePlanId: schedulePlan.Id);
								schedulePlan.LastStartedJobId = job.Id;
							}
							catch (Exception scex)
							{
								throw new Exception($"Schedule plan '{schedulePlan.Name}' failed to create job.", scex);
							}
						}
						UpdateSchedulePlanShort(schedulePlan);
					}
				}
				catch (Exception ex)
				{
					// Error logging (replacing monolith's DbContext.CreateContext +
					// Log log = new Log() pattern at source lines 205-218)
					try
					{
						using (var secCtx = SecurityContext.OpenSystemScope())
						{
							_logger.LogError(ex, "ScheduleManager.Process error");
						}
					}
					catch (Exception logEx)
					{
						// Prevent logging failures from crashing the schedule loop
						System.Diagnostics.Debug.WriteLine(
							$"ScheduleManager.Process: Failed to log error: {logEx.Message}. Original: {ex.Message}");
					}
				}
				finally
				{
					// Polling interval (preserved from source line 223)
					Thread.Sleep(12000);
				}
			}
		}

		#endregion

		#region <--- Asynchronous Processing Loop --->

		/// <summary>
		/// Asynchronous schedule processing loop with cooperative cancellation.
		/// Adapted from monolith source lines 228-375
		/// (<c>ProcessSchedulesAsync(CancellationToken)</c>).
		///
		/// Changes from monolith:
		/// - Initial delay moved to <see cref="ExecuteAsync"/> (was inline
		///   <c>#if !DEBUG Task.Delay(120000)</c>).
		/// - <c>JobManager.Current.CreateJob()</c> replaced with DI-resolved
		///   JobManager.
		/// - <c>DbContext.CreateContext/CloseContext</c> + <c>Log</c> replaced
		///   with <c>_logger</c>.
		/// - Method signature changed from <c>internal async void</c> to
		///   <c>private async Task</c> for proper async semantics.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token for cooperative
		/// shutdown.</param>
		private async Task ProcessSchedulesLoopAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					// Get ready for execution schedules
					// (preserved from source line 243)
					List<SchedulePlan> schedulePlans = JobService.GetReadyForExecutionScheduledPlans();

					// foreach schedule if it's time create a job and save it to db
					// (preserved from source lines 246-348)
					foreach (var schedulePlan in schedulePlans)
					{
						// Run new job if last one is finished or canceled
						// (preserved from source lines 249-251)
						bool startNewJob = true;
						if (schedulePlan.LastStartedJobId.HasValue)
							startNewJob = JobService.IsJobFinished(schedulePlan.LastStartedJobId.Value);

						// Calculate next schedule run time and update
						// (preserved from source lines 254-333)
						switch (schedulePlan.Type)
						{
							case SchedulePlanType.Interval:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime startDate = DateTime.UtcNow;

									DateTime? nextActivation = FindIntervalSchedulePlanNextTriggerDate(schedulePlan, startDate, schedulePlan.LastTriggerTime);
									if (nextActivation.HasValue)
									{
										schedulePlan.NextTriggerTime = nextActivation.Value;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Daily:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime startDate = DateTime.UtcNow;

									DateTime? nextActivation = FindDailySchedulePlanNextTriggerDate(schedulePlan, startDate.AddMinutes(1),
											schedulePlan.StartDate.HasValue ? schedulePlan.StartDate.Value : startDate);
									if (nextActivation.HasValue)
									{
										schedulePlan.NextTriggerTime = nextActivation.Value;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Weekly:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime nextActivation = schedulePlan.LastTriggerTime.HasValue
																  ? schedulePlan.LastTriggerTime.Value.AddDays(7)
																  : DateTime.UtcNow.AddDays(7);

									if ((!schedulePlan.EndDate.HasValue) || (nextActivation < schedulePlan.EndDate.Value))
									{
										schedulePlan.NextTriggerTime = nextActivation;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}

									break;
								}
							case SchedulePlanType.Monthly:
								{
									if (startNewJob)
										schedulePlan.LastTriggerTime = DateTime.UtcNow;

									DateTime nextActivation = schedulePlan.LastTriggerTime.HasValue
																  ? schedulePlan.LastTriggerTime.Value.AddMonths(1)
																  : DateTime.UtcNow.AddMonths(1);
									if ((!schedulePlan.EndDate.HasValue) || (nextActivation < schedulePlan.EndDate.Value))
									{
										schedulePlan.NextTriggerTime = nextActivation;
									}
									else
									{
										schedulePlan.NextTriggerTime = null;
									}
									break;
								}
						}

						// Create job if ready (preserved from source lines 335-346)
						if (startNewJob)
						{
							try
							{
								var jobManager = _serviceProvider.GetRequiredService<JobManager>();
								Job job = jobManager.CreateJob(schedulePlan.JobType.Id, schedulePlan.JobAttributes, schedulePlanId: schedulePlan.Id);
								schedulePlan.LastStartedJobId = job.Id;
							}
							catch (Exception scex)
							{
								throw new Exception($"Schedule plan '{schedulePlan.Name}' failed to create job.", scex);
							}
						}
						UpdateSchedulePlanShort(schedulePlan);
					}
				}
				catch (Exception ex)
				{
					// Error logging (replacing monolith's DbContext.CreateContext +
					// Log log = new Log() pattern at source lines 354-367)
					try
					{
						using (var secCtx = SecurityContext.OpenSystemScope())
						{
							_logger.LogError(ex, "ScheduleManager.Process error");
						}
					}
					catch (Exception logEx)
					{
						// Prevent logging failures from crashing the schedule loop
						System.Diagnostics.Debug.WriteLine(
							$"ScheduleManager.ProcessSchedulesLoopAsync: Failed to log error: {logEx.Message}. Original: {ex.Message}");
					}
				}
				finally
				{
					// Polling interval (preserved from source line 372)
					try { await Task.Delay(10000, stoppingToken); } catch (TaskCanceledException) { }
				}
			}
		}

		#endregion

		#region <--- Trigger Date Calculation --->

		/// <summary>
		/// Computes the next trigger date for a schedule plan based on its type.
		/// Dispatches to type-specific calculation methods.
		///
		/// Preserved EXACTLY from monolith source lines 377-410.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan to compute trigger for.</param>
		/// <returns>The next trigger date/time, or null if expired/invalid.</returns>
		public DateTime? FindSchedulePlanNextTriggerDate(SchedulePlan schedulePlan)
		{
			SchedulePlanType planType = schedulePlan.Type;
			DateTime nowDateTime = DateTime.UtcNow;//.AddMinutes(1);
			DateTime startingDate;
			if (schedulePlan.StartDate.HasValue)//if day is selected then 
			{
				startingDate = schedulePlan.StartDate.Value;
			}
			else
			{
				startingDate = nowDateTime;
			}
			switch (planType)
			{
				case SchedulePlanType.Interval:
					{
						return FindIntervalSchedulePlanNextTriggerDate(schedulePlan, nowDateTime, schedulePlan.LastTriggerTime);
					}
				case SchedulePlanType.Daily:
					{
						return FindDailySchedulePlanNextTriggerDate(schedulePlan, nowDateTime, startingDate);
					}
				case SchedulePlanType.Weekly:
					{
						return FindWeeklySchedulePlanNextTriggerDate(schedulePlan, nowDateTime, startingDate);
					}
				case SchedulePlanType.Monthly:
					{
						return FindMonthlySchedulePlanNextTriggerDate(schedulePlan, nowDateTime, startingDate);
					}
			}
			return null;
		}

		/// <summary>
		/// Calculates the next trigger date for an interval-based schedule plan.
		/// Considers day-of-week constraints, timespan intervals, and end date
		/// expiry.
		///
		/// Preserved EXACTLY from monolith source lines 412-476.
		/// </summary>
		/// <param name="intervalPlan">The interval schedule plan.</param>
		/// <param name="nowDateTime">Current UTC date/time.</param>
		/// <param name="lastExecution">Last execution time, if any.</param>
		/// <returns>The next trigger date/time, or null if expired/no match.</returns>
		private DateTime? FindIntervalSchedulePlanNextTriggerDate(SchedulePlan intervalPlan, DateTime nowDateTime, DateTime? lastExecution)
		{
			if (intervalPlan.ScheduledDays == null)
				intervalPlan.ScheduledDays = new SchedulePlanDaysOfWeek();

			var daysOfWeek = intervalPlan.ScheduledDays;

			//if interval is <=0 then can't find match
			if (intervalPlan.IntervalInMinutes <= 0)
			{
				return null;
			}

			DateTime startingDate = lastExecution.HasValue ? lastExecution.Value.AddMinutes(intervalPlan.IntervalInMinutes.Value) : DateTime.UtcNow;
			try
			{
				while (true)
				{
					//check for expired interval
					if (intervalPlan.EndDate.HasValue)
					{
						if (intervalPlan.EndDate.Value < startingDate)
						{
							return null;
						}
					}

					int timeAsInt = startingDate.Hour * 60 + startingDate.Minute;
					bool isIntervalConnectedToFirstDay = false;
					if (intervalPlan.StartTimespan.HasValue)
					{
						isIntervalConnectedToFirstDay = ((intervalPlan.StartTimespan.Value > intervalPlan.EndTimespan.Value) &&
															   ((0 < timeAsInt) && (timeAsInt <= intervalPlan.EndTimespan.Value)));
					}

					if (IsTimeInTimespanInterval(startingDate, intervalPlan.StartTimespan, intervalPlan.EndTimespan))
					{
						if (IsDayUsedInSchedulePlan(startingDate, daysOfWeek, isIntervalConnectedToFirstDay))
							return startingDate;
						else
							startingDate = startingDate.AddDays(1);

						continue;
					}
					else //step
					{
						DateTime startTimespan = new DateTime(startingDate.Year, startingDate.Month, startingDate.Day, 0, 0, 0, DateTimeKind.Utc);
						startTimespan = startTimespan.AddMinutes(intervalPlan.StartTimespan.Value);

						if (nowDateTime <= startTimespan && startingDate <= startTimespan)
						{
							startingDate = startTimespan;
						}
						else
						{
							startingDate = startTimespan.AddDays(1);
						}
					}
				}
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Calculates the next trigger date for a daily schedule plan.
		/// Steps through days checking day-of-week constraints and end date expiry.
		///
		/// Preserved EXACTLY from monolith source lines 530-566.
		/// </summary>
		/// <param name="dailyPlan">The daily schedule plan.</param>
		/// <param name="nowDateTime">Current UTC date/time.</param>
		/// <param name="startDate">Starting date for calculation.</param>
		/// <returns>The next trigger date/time, or null if expired/no match.</returns>
		private DateTime? FindDailySchedulePlanNextTriggerDate(SchedulePlan dailyPlan, DateTime nowDateTime, DateTime startDate)
		{
			if (dailyPlan.ScheduledDays == null)
				dailyPlan.ScheduledDays = new SchedulePlanDaysOfWeek();

			var daysOfWeek = dailyPlan.ScheduledDays;

			DateTime startingDate = startDate;
			try
			{
				while (true)
				{
					//check for expired interval
					if (dailyPlan.EndDate.HasValue)
					{
						if (dailyPlan.EndDate.Value < startingDate)
						{
							return null;
						}
					}

					DateTime movedTime = startingDate.AddSeconds(10);
					if (movedTime >= nowDateTime && IsDayUsedInSchedulePlan(startingDate, daysOfWeek, false))
					{
						return startingDate;
					}
					else //step
					{
						startingDate = startingDate.AddDays(1);
					}
				}
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Calculates the next trigger date for a weekly schedule plan.
		/// Steps through weeks (7-day intervals) checking end date expiry.
		///
		/// Preserved EXACTLY from monolith source lines 568-598.
		/// </summary>
		/// <param name="weeklyPlan">The weekly schedule plan.</param>
		/// <param name="nowDateTime">Current UTC date/time.</param>
		/// <param name="startDate">Starting date for calculation.</param>
		/// <returns>The next trigger date/time, or null if expired.</returns>
		private DateTime? FindWeeklySchedulePlanNextTriggerDate(SchedulePlan weeklyPlan, DateTime nowDateTime, DateTime startDate)
		{
			DateTime startingDate = startDate;
			try
			{
				while (true)
				{
					//check for expired interval
					if (weeklyPlan.EndDate.HasValue)
					{
						if (weeklyPlan.EndDate.Value < startingDate)
						{
							return null;
						}
					}
					DateTime movedTime = startingDate.AddSeconds(10);
					if (movedTime >= nowDateTime)
					{
						return startingDate;
					}
					else //step
					{
						startingDate = startingDate.AddDays(7);
					}
				}
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Calculates the next trigger date for a monthly schedule plan.
		/// Steps through months checking end date expiry.
		///
		/// Preserved EXACTLY from monolith source lines 600-630.
		/// </summary>
		/// <param name="monthlyPlan">The monthly schedule plan.</param>
		/// <param name="nowDateTime">Current UTC date/time.</param>
		/// <param name="startDate">Starting date for calculation.</param>
		/// <returns>The next trigger date/time, or null if expired.</returns>
		private DateTime? FindMonthlySchedulePlanNextTriggerDate(SchedulePlan monthlyPlan, DateTime nowDateTime, DateTime startDate)
		{
			DateTime startingDate = startDate;
			try
			{
				while (true)
				{
					//check for expired interval
					if (monthlyPlan.EndDate.HasValue)
					{
						if (monthlyPlan.EndDate.Value < startingDate)
						{
							return null;
						}
					}
					DateTime movedTime = startingDate.AddSeconds(10);
					if (movedTime >= nowDateTime)
					{
						return startingDate;
					}
					else //step
					{
						startingDate = startingDate.AddMonths(1);
					}
				}
			}
			catch
			{
				return null;
			}
		}

		#endregion

		#region <--- Schedule Helpers --->

		/// <summary>
		/// Checks whether the given day is selected in the schedule plan's
		/// days-of-week configuration. Supports time-connected-to-first-day
		/// offset where the effective day is shifted back by one.
		///
		/// Preserved EXACTLY from monolith source lines 632-702.
		/// </summary>
		/// <param name="checkedDay">The date to check.</param>
		/// <param name="selectedDays">The schedule plan's day-of-week
		/// configuration.</param>
		/// <param name="isTimeConnectedToFirstDay">Whether to apply the
		/// day-offset logic for overnight intervals.</param>
		/// <returns><c>true</c> if the day is used in the schedule plan.</returns>
		private bool IsDayUsedInSchedulePlan(DateTime checkedDay, SchedulePlanDaysOfWeek selectedDays, bool isTimeConnectedToFirstDay)
		{
			DateTime dayToCheck = checkedDay;
			DayOfWeek dayOfWeek = dayToCheck.DayOfWeek;
			if (isTimeConnectedToFirstDay)
			{
				dayToCheck = dayToCheck.AddDays(-1);
				dayOfWeek = dayToCheck.DayOfWeek;
			}
			switch (dayOfWeek)
			{
				case DayOfWeek.Sunday:
					{
						if (selectedDays.ScheduledOnSunday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Monday:
					{
						if (selectedDays.ScheduledOnMonday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Tuesday:
					{
						if (selectedDays.ScheduledOnTuesday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Wednesday:
					{
						if (selectedDays.ScheduledOnWednesday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Thursday:
					{
						if (selectedDays.ScheduledOnThursday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Friday:
					{
						if (selectedDays.ScheduledOnFriday)
						{
							return true;
						}
						break;
					}
				case DayOfWeek.Saturday:
					{
						if (selectedDays.ScheduledOnSaturday)
						{
							return true;
						}
						break;
					}
			}

			return false;
		}

		/// <summary>
		/// Checks whether the given date's time-of-day falls within the
		/// specified timespan interval. Handles normal intervals
		/// (start &lt; end) and day-overlap intervals (start &gt; end, wrapping
		/// past midnight).
		///
		/// Preserved EXACTLY from monolith source lines 704-722.
		/// </summary>
		/// <param name="date">The date/time to check.</param>
		/// <param name="startTimespan">Start of timespan in minutes from
		/// midnight, or null for no constraint.</param>
		/// <param name="endTimespan">End of timespan in minutes from midnight.</param>
		/// <returns><c>true</c> if the time is within the interval.</returns>
		private bool IsTimeInTimespanInterval(DateTime date, int? startTimespan, int? endTimespan)
		{
			int timeAsInt = date.Hour * 60 + date.Minute;
			//if no time span interval then everything is ok
			if (!startTimespan.HasValue)
			{
				return true;
			}

			if (startTimespan < endTimespan) //normal situation start(200) - end(1000)
			{
				return ((startTimespan <= timeAsInt) && (timeAsInt <= endTimespan));
			}
			else //day overlap start(1000) - end(200)
			{
				return (((startTimespan <= timeAsInt) && (timeAsInt <= 1440)) ||
						 ((0 < timeAsInt) && (timeAsInt <= endTimespan)));
			}
		}

		#endregion
	}
}
