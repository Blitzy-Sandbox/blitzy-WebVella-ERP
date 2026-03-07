using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Service-scoped job manager for the Core Platform microservice.
	/// Maintains the job type registry, handles crash recovery (marking interrupted
	/// Running jobs as Aborted), provides job CRUD via <see cref="JobDataService"/>,
	/// and runs the dispatcher loop that polls for pending jobs and feeds them to
	/// <see cref="JobPool"/>.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Jobs.JobManager</c> (303 lines)
	/// with the following architectural changes:
	/// - Static singleton pattern (<c>JobManager.Current</c>) removed; registered as singleton in DI.
	/// - Inherits <see cref="BackgroundService"/> to merge the monolith's separate
	///   <c>ErpJobProcessService</c> into this class (eliminating the polling-for-Current pattern).
	/// - <c>JobManager.Settings</c> replaced with <see cref="IConfiguration"/> injection.
	/// - <c>WebVella.Erp.Diagnostics.Log</c> replaced with <see cref="ILogger{T}"/>.
	/// - <c>JobPool.Current</c> replaced with injected <see cref="JobPool"/> instance.
	/// - <c>AppDomain.CurrentDomain.GetAssemblies()</c> replaced with scoped assembly scanning.
	/// - All original business logic (crash recovery, type registration, job CRUD, dispatcher) preserved exactly.
	/// </summary>
	public class JobManager : BackgroundService
	{
		#region <--- Private Fields --->

		/// <summary>
		/// Job type registry. Instance field replacing the monolith's static
		/// <c>public static List&lt;JobType&gt; JobTypes</c>.
		/// </summary>
		private readonly List<JobType> _jobTypes;

		/// <summary>
		/// Job data persistence service using direct PostgreSQL access.
		/// Created from configuration connection string in the constructor.
		/// </summary>
		private readonly JobDataService _jobService;

		/// <summary>
		/// Bounded in-process job executor injected via DI.
		/// Replaces the monolith's static <c>JobPool.Current</c>.
		/// </summary>
		private readonly JobPool _jobPool;

		/// <summary>
		/// Structured logger replacing the monolith's <c>WebVella.Erp.Diagnostics.Log</c> class.
		/// </summary>
		private readonly ILogger<JobManager> _logger;

		/// <summary>
		/// Service provider for creating DI scopes during job operations.
		/// </summary>
		private readonly IServiceProvider _serviceProvider;

		/// <summary>
		/// Application configuration for reading per-service settings.
		/// </summary>
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Whether the job system is enabled.
		/// Read from <c>Jobs:Enabled</c> configuration (default false).
		/// Replaces monolith's <c>Settings.Enabled</c>.
		/// </summary>
		private readonly bool _enabled;

		/// <summary>
		/// Startup delay in seconds before the dispatcher begins processing.
		/// Read from <c>Jobs:StartupDelaySeconds</c> configuration (default 120).
		/// Replaces the monolith's <c>#if DEBUG 10s / RELEASE 120s</c> pattern.
		/// </summary>
		private readonly int _startupDelaySeconds;

		/// <summary>
		/// Optional additional assemblies to scan for job types beyond the
		/// executing assembly. Allows external assemblies to register jobs.
		/// </summary>
		private IEnumerable<Assembly> _additionalAssemblies;

		#endregion

		#region <--- Constructor --->

		/// <summary>
		/// Initializes a new instance of the <see cref="JobManager"/> class with dependency injection.
		/// Replaces the monolith's private constructors and static <c>Initialize()</c> method.
		///
		/// Performs crash recovery on construction: all jobs with Running status
		/// are marked as Aborted (preserving the exact monolith behavior from source lines 32-41).
		/// </summary>
		/// <param name="configuration">Application configuration for reading connection strings and job settings.</param>
		/// <param name="logger">Structured logger for error and diagnostic logging.</param>
		/// <param name="jobPool">Bounded in-process job executor for dispatching jobs.</param>
		/// <param name="serviceProvider">Service provider for resolving scoped services.</param>
		public JobManager(
			IConfiguration configuration,
			ILogger<JobManager> logger,
			JobPool jobPool,
			IServiceProvider serviceProvider)
		{
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_jobPool = jobPool ?? throw new ArgumentNullException(nameof(jobPool));
			_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

			// Read per-service configuration (replacing monolith's static JobManagerSettings)
			_enabled = configuration.GetValue<bool>("Jobs:Enabled", false);
			_startupDelaySeconds = configuration.GetValue<int>("Jobs:StartupDelaySeconds", 120);
			if (_startupDelaySeconds < 0)
				_startupDelaySeconds = 120;

			// Initialize job type registry (replacing monolith's static JobTypes list)
			_jobTypes = new List<JobType>();

			// Create JobDataService with connection string from configuration
			// (replacing monolith's: new JobDataService(Settings))
			var settings = new JobManagerSettings
			{
				DbConnectionString = configuration["ConnectionStrings:Default"],
				Enabled = _enabled
			};
			_jobService = new JobDataService(settings);

			// === CRASH RECOVERY ===
			// Preserved EXACTLY from monolith source lines 32-41.
			// Get all jobs with status Running and set them to status Aborted.
			// This handles the scenario where the service crashed while jobs were executing.
			var runningJobs = _jobService.GetRunningJobs();
			foreach (var job in runningJobs)
			{
				job.Status = JobStatus.Aborted;
				job.AbortedBy = Guid.Empty; // by system
				job.FinishedOn = DateTime.UtcNow;
				_jobService.UpdateJob(job);
			}

			_logger.LogInformation(
				"JobManager initialized. Enabled={Enabled}, StartupDelay={StartupDelay}s, CrashRecovery={AbortedCount} jobs aborted.",
				_enabled, _startupDelaySeconds, runningJobs.Count);
		}

		#endregion

		#region <--- Public Properties --->

		/// <summary>
		/// Gets the registered job types. Read-only access for ScheduleManager and other consumers.
		/// Replaces the monolith's <c>public static List&lt;JobType&gt; JobTypes</c>.
		/// </summary>
		public List<JobType> JobTypes => _jobTypes;

		/// <summary>
		/// Gets whether the job system is enabled.
		/// Read from <c>Jobs:Enabled</c> configuration.
		/// </summary>
		public bool Enabled => _enabled;

		#endregion

		#region <--- Job Type Registration --->

		/// <summary>
		/// Scans assemblies for classes decorated with <see cref="JobAttribute"/>
		/// that derive from <see cref="ErpJob"/>, and registers each discovered type.
		///
		/// Adapted from monolith source lines 56-83. Critical change: replaces
		/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> scanning ALL assemblies
		/// with scoped scanning of Core service assemblies only (executing assembly
		/// plus any additional assemblies provided via <see cref="SetAdditionalAssemblies"/>).
		/// </summary>
		public void RegisterJobTypes()
		{
			// Scope to Core service assemblies, not all AppDomain assemblies
			// (replacing monolith's: AppDomain.CurrentDomain.GetAssemblies())
			var assemblies = new[] { Assembly.GetExecutingAssembly() }
				.Concat(_additionalAssemblies ?? Enumerable.Empty<Assembly>())
				.Where(a => !(a.FullName.ToLowerInvariant().StartsWith("microsoft.")
					|| a.FullName.ToLowerInvariant().StartsWith("system.")));

			// Inner logic preserved EXACTLY from monolith source lines 61-82
			foreach (var assembly in assemblies)
			{
				foreach (Type type in assembly.GetTypes())
				{
					if (!type.IsSubclassOf(typeof(ErpJob)))
						continue;

					var attributes = type.GetCustomAttributes(typeof(JobAttribute), true);
					if (attributes.Length != 1)
						continue;

					var attribute = attributes[0] as JobAttribute;
					JobType internalJobType = new JobType();
					internalJobType.Id = attribute.Id;
					internalJobType.Name = attribute.Name;
					internalJobType.DefaultPriority = (JobPriority)((int)attribute.DefaultPriority);
					internalJobType.AllowSingleInstance = attribute.AllowSingleInstance;
					internalJobType.CompleteClassName = type.FullName;
					internalJobType.ErpJobType = type;
					RegisterJobType(internalJobType);
				}
			}
		}

		/// <summary>
		/// Sets additional assemblies to be scanned during <see cref="RegisterJobTypes"/>.
		/// Allows external service plugins to register their job types with the Core service.
		/// </summary>
		/// <param name="assemblies">Assemblies to scan for <see cref="ErpJob"/> derivatives.</param>
		public void SetAdditionalAssemblies(IEnumerable<Assembly> assemblies)
		{
			_additionalAssemblies = assemblies;
		}

		/// <summary>
		/// Registers a single job type in the type registry.
		/// Validates uniqueness by name (case-insensitive) and ID.
		///
		/// Preserved EXACTLY from monolith source lines 85-98.
		/// Change: <c>Log log = new Log(); log.Create(...)</c> replaced with
		/// <c>_logger.LogError(...)</c>.
		/// </summary>
		/// <param name="type">The job type to register.</param>
		/// <returns><c>true</c> if registration succeeded; <c>false</c> if a duplicate name was found.</returns>
		public bool RegisterJobType(JobType type)
		{
			// Case-insensitive name duplicate check (preserved from source line 87)
			if (_jobTypes.Any(t => t.Name.ToLowerInvariant() == type.Name.ToLowerInvariant()))
			{
				// Error logging on duplicate (replacing monolith's Log class with ILogger)
				_logger.LogError(
					"Register type failed! Type with name '{TypeName}' already exists.",
					type.Name);
				return false;
			}

			// Add to list if Id not already present (preserved from source lines 94-95)
			if (!_jobTypes.Any(t => t.Id == type.Id))
				_jobTypes.Add(type);

			return true;
		}

		#endregion

		#region <--- Job CRUD --->

		/// <summary>
		/// Creates a new job of the specified type with optional attributes and scheduling metadata.
		///
		/// Preserved EXACTLY from monolith source lines 100-127.
		/// Change: <c>Log log = new Log(); log.Create(...)</c> replaced with
		/// <c>_logger.LogError(...)</c>.
		/// </summary>
		/// <param name="typeId">The registered job type identifier.</param>
		/// <param name="attributes">Optional dynamic attributes passed to the job execution context.</param>
		/// <param name="priority">Job execution priority; normalized to type default if invalid.</param>
		/// <param name="creatorId">Optional user who created the job.</param>
		/// <param name="schedulePlanId">Optional schedule plan that triggered this job.</param>
		/// <param name="jobId">Optional explicit job ID; auto-generated if null.</param>
		/// <returns>The created <see cref="Job"/> instance, or <c>null</c> if the type was not found.</returns>
		public Job CreateJob(Guid typeId, dynamic attributes = null, JobPriority priority = 0,
			Guid? creatorId = null, Guid? schedulePlanId = null, Guid? jobId = null)
		{
			// Find type by typeId in JobTypes (preserved from source line 102)
			JobType type = JobTypes.FirstOrDefault(t => t.Id == typeId);
			if (type == null)
			{
				// Error logging on type not found (replacing monolith's Log class)
				_logger.LogError(
					"Create job failed! Type with id '{TypeId}' can not be found.",
					typeId);
				return null;
			}

			// Priority normalization (preserved from source lines 110-111)
			if (!Enum.IsDefined(typeof(JobPriority), priority))
				priority = type.DefaultPriority;

			// Job construction with all fields (preserved from source lines 113-124)
			Job job = new Job();
			job.Id = jobId.HasValue ? jobId.Value : Guid.NewGuid();
			job.TypeId = type.Id;
			job.Type = type;
			job.TypeName = type.Name;
			job.CompleteClassName = type.CompleteClassName;
			job.Status = JobStatus.Pending;
			job.Priority = priority;
			job.Attributes = attributes;
			job.CreatedBy = creatorId;
			job.LastModifiedBy = creatorId;
			job.SchedulePlanId = schedulePlanId;

			return _jobService.CreateJob(job);
		}

		/// <summary>
		/// Updates an existing job's state in the database.
		/// Preserved EXACTLY from monolith source lines 129-132.
		/// </summary>
		/// <param name="job">The job with updated fields.</param>
		/// <returns><c>true</c> if the update succeeded.</returns>
		public bool UpdateJob(Job job)
		{
			return _jobService.UpdateJob(job);
		}

		/// <summary>
		/// Retrieves a job by its unique identifier.
		/// Preserved EXACTLY from monolith source lines 134-137.
		/// </summary>
		/// <param name="jobId">The job's unique identifier.</param>
		/// <returns>The <see cref="Job"/> instance, or <c>null</c> if not found.</returns>
		public Job GetJob(Guid jobId)
		{
			return _jobService.GetJob(jobId);
		}

		/// <summary>
		/// Retrieves a paginated, filtered list of jobs with total count.
		/// Preserved EXACTLY from monolith source lines 139-144 including all
		/// filter parameters and pagination support.
		/// </summary>
		/// <param name="totalCount">Output: total number of matching jobs (for pagination).</param>
		/// <param name="startFromDate">Optional filter: job started on or after this date.</param>
		/// <param name="startToDate">Optional filter: job started on or before this date.</param>
		/// <param name="finishedFromDate">Optional filter: job finished on or after this date.</param>
		/// <param name="finishedToDate">Optional filter: job finished on or before this date.</param>
		/// <param name="typeName">Optional filter: job type name (ILIKE search).</param>
		/// <param name="status">Optional filter: job status integer value.</param>
		/// <param name="priority">Optional filter: job priority integer value.</param>
		/// <param name="schedulePlanId">Optional filter: originating schedule plan ID.</param>
		/// <param name="page">Optional: page number for pagination (1-based).</param>
		/// <param name="pageSize">Optional: number of records per page.</param>
		/// <returns>List of matching <see cref="Job"/> instances.</returns>
		public List<Job> GetJobs(out int totalCount, DateTime? startFromDate = null,
			DateTime? startToDate = null, DateTime? finishedFromDate = null,
			DateTime? finishedToDate = null, string typeName = null, int? status = null,
			int? priority = null, Guid? schedulePlanId = null, int? page = null,
			int? pageSize = null)
		{
			totalCount = (int)_jobService.GetJobsTotalCount(startFromDate, startToDate,
				finishedFromDate, finishedToDate, typeName, status, priority, schedulePlanId);
			return _jobService.GetJobs(startFromDate, startToDate, finishedFromDate,
				finishedToDate, typeName, status, priority, schedulePlanId, page, pageSize);
		}

		#endregion

		#region <--- Fire-and-Forget Processing --->

		/// <summary>
		/// Starts the synchronous job processing loop on a background thread (fire-and-forget).
		/// Preserved from monolith source lines 146-149.
		/// </summary>
		public void ProcessJobsAsync()
		{
			Task.Run(() => Process());
		}

		/// <summary>
		/// Synchronous job processing loop with Thread.Sleep polling.
		/// Preserved from monolith source lines 151-226.
		///
		/// Changes:
		/// - <c>Settings.Enabled</c> replaced with <c>_enabled</c>.
		/// - <c>#if DEBUG 10s / RELEASE 120s</c> replaced with configurable <c>_startupDelaySeconds</c>.
		/// - <c>JobPool.Current</c> replaced with injected <c>_jobPool</c>.
		/// - <c>DbContext.CreateContext/CloseContext</c> + <c>Log</c> replaced with <c>_logger</c>.
		/// </summary>
		private void Process()
		{
			// Enabled check (preserved from source line 153)
			if (!_enabled)
				return;

			// Configurable initial sleep time
			// (replacing monolith's #if DEBUG 10s / RELEASE 120s at source lines 157-161)
			Thread.Sleep(_startupDelaySeconds * 1000);

			while (true)
			{
				try
				{
					// If there are free threads in the pool (replacing JobPool.Current)
					if (_jobPool.HasFreeThreads)
					{
						// Get pending jobs (limit the count of the returned jobs to be <= to count of the free threads)
						List<Job> pendingJobs = _jobService.GetPendingJobs(_jobPool.FreeThreadsCount);

						foreach (var job in pendingJobs)
						{
							try
							{
								if (job.Type.AllowSingleInstance && _jobPool.HasJobFromTypeInThePool(job.Type.Id))
									continue;

								_jobPool.RunJobAsync(job);
							}
							catch (Exception ex)
							{
								// Error logging (replacing monolith's DbContext.CreateContext + Log pattern)
								string jobId = job != null ? job.Id.ToString() : "null";
								string jobType = job != null && job.Type != null ? job.Type.Name : "null";
								_logger.LogError(ex,
									"Start job with id[{JobId}] and type [{JobType}] failed!",
									jobId, jobType);
							}
						}
					}
				}
				catch (Exception ex)
				{
					// Outer exception logging (replacing monolith's DbContext + Log pattern)
					_logger.LogError(ex, "JobManager.Process error");
				}
				finally
				{
					Thread.Sleep(12000);
				}
			}
		}

		#endregion

		#region <--- Async Processing Loop --->

		/// <summary>
		/// Asynchronous job processing loop with cooperative cancellation.
		/// Adapted from monolith source lines 228-301.
		///
		/// Changes:
		/// - <c>Settings.Enabled</c> replaced with <c>_enabled</c>.
		/// - <c>#if !DEBUG Task.Delay(120000)</c> replaced with configurable startup delay.
		/// - <c>JobPool.Current</c> replaced with injected <c>_jobPool</c>.
		/// - <c>DbContext.CreateContext/CloseContext</c> + <c>Log</c> replaced with <c>_logger</c>.
		///
		/// Note: The monolith declared this as <c>public async void</c>. In the refactored version
		/// this is kept as <c>public async Task</c> for proper async semantics, but the method
		/// name is preserved for API compatibility with ScheduleManager and other callers.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token for cooperative shutdown.</param>
		public async Task ProcessJobsAsync(CancellationToken stoppingToken)
		{
			// Enabled check (preserved from source line 230)
			if (!_enabled)
				return;

			// Configurable startup delay
			// (replacing monolith's #if !DEBUG Task.Delay(120000) at source lines 234-236)
			try
			{
				await Task.Delay(_startupDelaySeconds * 1000, stoppingToken);
			}
			catch (TaskCanceledException)
			{
				return;
			}

			// Main processing loop (preserved from source line 238)
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					// If there are free threads in the pool
					// (replacing JobPool.Current.HasFreeThreads at source line 243)
					if (_jobPool.HasFreeThreads)
					{
						// Get pending jobs (limit the count of the returned jobs
						// to be <= to count of the free threads)
						// (replacing JobPool.Current.FreeThreadsCount at source line 246)
						List<Job> pendingJobs = _jobService.GetPendingJobs(_jobPool.FreeThreadsCount);

						foreach (var job in pendingJobs)
						{
							try
							{
								// Single instance check
								// (replacing JobPool.Current.HasJobFromTypeInThePool at source line 252)
								if (job.Type.AllowSingleInstance && _jobPool.HasJobFromTypeInThePool(job.Type.Id))
									continue;

								// Dispatch job for execution
								// (replacing JobPool.Current.RunJobAsync at source line 255)
								_jobPool.RunJobAsync(job);
							}
							catch (Exception ex)
							{
								// Error logging for individual job dispatch failures
								// (replacing monolith's DbContext.CreateContext + Log pattern
								// at source lines 259-269)
								string jobId = job != null ? job.Id.ToString() : "null";
								string jobType = job != null && job.Type != null ? job.Type.Name : "null";
								_logger.LogError(ex,
									"Start job with id[{JobId}] and type [{JobType}] failed!",
									jobId, jobType);
							}
						}
					}
				}
				catch (Exception ex)
				{
					// Outer exception logging
					// (replacing monolith's DbContext.CreateContext + Log pattern
					// at source lines 279-294)
					_logger.LogError(ex, "JobManager.Process error");
				}

				// Polling interval (preserved from source line 298)
				// Placed outside try/catch/finally to allow break on cancellation.
				try
				{
					await Task.Delay(10000, stoppingToken);
				}
				catch (TaskCanceledException)
				{
					break;
				}
			}
		}

		#endregion

		#region <--- BackgroundService Integration --->

		/// <summary>
		/// Entry point for the ASP.NET Core hosted service lifecycle.
		/// Merges the monolith's <c>ErpJobProcessService</c> (from ErpBackgroundServices.cs
		/// lines 24-39) directly into JobManager, eliminating the separate hosted service
		/// that polled for <c>JobManager.Current != null</c>.
		///
		/// This override delegates to <see cref="ProcessJobsAsync(CancellationToken)"/>
		/// which handles startup delay and the main processing loop.
		/// </summary>
		/// <param name="stoppingToken">Cancellation token triggered on application shutdown.</param>
		/// <returns>A task representing the long-running background operation.</returns>
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!_enabled)
			{
				_logger.LogInformation("JobManager background service is disabled (Jobs:Enabled=false). Exiting.");
				return;
			}

			_logger.LogInformation("JobManager background service starting with {DelaySeconds}s startup delay.", _startupDelaySeconds);

			// Delegate to the main processing loop
			await ProcessJobsAsync(stoppingToken);

			_logger.LogInformation("JobManager background service stopped.");
		}

		#endregion
	}
}
