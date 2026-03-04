using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Database;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Bounded in-process job executor for the Core Platform microservice.
	/// Tracks active <see cref="JobContext"/> instances, prevents duplicate execution,
	/// transitions job states (Running → Finished/Failed), and manages a configurable
	/// thread pool.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Jobs.JobPool</c> with the following changes:
	/// - Static singleton pattern removed; registered as a singleton in DI container.
	/// - <c>JobManager.Settings</c> replaced with <see cref="IConfiguration"/> injection.
	/// - <c>WebVella.Erp.Diagnostics.Log</c> replaced with <see cref="ILogger{T}"/>.
	/// - <c>DbContext.CreateContext/CloseContext</c> replaced with <see cref="CoreDbContext"/>.
	/// - Max thread pool count configurable via <c>Jobs:MaxThreadPoolSize</c> (default 20).
	/// - All original business logic (duplicate check, state transitions, abort, type check) preserved exactly.
	/// </summary>
	public class JobPool
	{
		/// <summary>
		/// Static lock object for thread-safe access to the pool list.
		/// Remains static to guarantee mutual exclusion across all threads,
		/// matching the monolith's locking semantics.
		/// </summary>
		private static readonly object _lockObj = new object();

		/// <summary>
		/// Maximum number of concurrent job execution threads.
		/// Read from <c>Jobs:MaxThreadPoolSize</c> configuration (default 20).
		/// Replaces the monolith's hardcoded <c>MAX_THREADS_POOL_COUNT = 20</c>.
		/// </summary>
		private readonly int _maxThreadPoolCount;

		/// <summary>
		/// List of currently executing job contexts.
		/// Instance field (not static) — scoped to this DI-registered singleton.
		/// </summary>
		private readonly List<JobContext> _pool;

		/// <summary>
		/// Configuration accessor for reading connection strings and job settings.
		/// Replaces the monolith's static <c>ErpSettings</c> and <c>JobManager.Settings</c>.
		/// </summary>
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Structured logger replacing the monolith's <c>WebVella.Erp.Diagnostics.Log</c> class.
		/// </summary>
		private readonly ILogger<JobPool> _logger;

		/// <summary>
		/// Service provider for creating DI scopes during job execution.
		/// Replaces the monolith's static <c>DbContext.CreateContext()</c> pattern.
		/// </summary>
		private readonly IServiceProvider _serviceProvider;

		/// <summary>
		/// Initializes a new instance of the <see cref="JobPool"/> class with dependency injection.
		/// Replaces the monolith's private parameterless constructor and static <c>Initialize()</c> method.
		/// </summary>
		/// <param name="configuration">Application configuration for reading <c>Jobs:MaxThreadPoolSize</c> and connection strings.</param>
		/// <param name="logger">Structured logger for error and diagnostic logging.</param>
		/// <param name="serviceProvider">Service provider for creating scoped execution contexts.</param>
		public JobPool(IConfiguration configuration, ILogger<JobPool> logger, IServiceProvider serviceProvider)
		{
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

			// Read configurable max thread pool size; default to 20 (matching monolith's MAX_THREADS_POOL_COUNT)
			_maxThreadPoolCount = configuration.GetValue<int>("Jobs:MaxThreadPoolSize", 20);
			if (_maxThreadPoolCount <= 0)
				_maxThreadPoolCount = 20;

			_pool = new List<JobContext>();
		}

		/// <summary>
		/// Gets a value indicating whether the pool has free threads available for job execution.
		/// Thread-safe — all pool access is protected by <c>_lockObj</c>.
		/// </summary>
		/// <remarks>
		/// Preserved exactly from monolith source lines 24-33.
		/// </remarks>
		public bool HasFreeThreads
		{
			get
			{
				lock (_lockObj)
				{
					return _pool.Count < _maxThreadPoolCount;
				}
			}
		}

		/// <summary>
		/// Gets the number of free threads available in the pool.
		/// Thread-safe — all pool access is protected by <c>_lockObj</c>.
		/// </summary>
		/// <remarks>
		/// Preserved exactly from monolith source lines 35-43.
		/// </remarks>
		public int FreeThreadsCount
		{
			get
			{
				lock (_lockObj)
				{
					return _maxThreadPoolCount - _pool.Count;
				}
			}
		}

		/// <summary>
		/// Schedules a job for asynchronous execution if the pool has capacity and
		/// the job is not already in the pool (duplicate prevention).
		/// Creates a <see cref="JobContext"/> and dispatches <see cref="Process"/> via <c>Task.Run</c>.
		/// </summary>
		/// <param name="job">The job to execute. Must have a valid <c>Id</c>, <c>Type</c>, and <c>Priority</c>.</param>
		/// <remarks>
		/// Preserved exactly from monolith source lines 56-77.
		/// Duplicate-prevention logic checks both job ID uniqueness and pool capacity atomically under lock.
		/// </remarks>
		public void RunJobAsync(Job job)
		{
			// Get pool count and if it is < of max_thread_pool_count create new context and start execute the job in new thread
			bool allowed = false;
			lock (_lockObj)
			{
				allowed = !_pool.Any(p => p.JobId == job.Id) && _pool.Count < _maxThreadPoolCount;
			}
			if (allowed)
			{
				// Job does not exist in the pool and the pool has free threads.
				JobContext context = new JobContext();
				context.JobId = job.Id;
				context.Aborted = false;
				context.Priority = job.Priority;
				context.Attributes = job.Attributes;
				context.Type = job.Type;

				Task.Run(() => Process(context));
			}
		}

		/// <summary>
		/// Executes a job within a managed lifecycle: transitions job state through
		/// Running → Finished (or Failed), manages database context scoping,
		/// establishes system security scope, and ensures pool cleanup.
		/// 
		/// This is the critical execution method that preserves all monolith business logic:
		/// - Job state transitions (Running, Finished, Failed)
		/// - Database context creation/closure via <see cref="CoreDbContext"/>
		/// - System security scope via <see cref="SecurityContext.OpenSystemScope"/>
		/// - TargetInvocationException unwrapping for reflection-based instantiation
		/// - Result capture from <see cref="JobContext.Result"/>
		/// - Error logging and job failure recording
		/// - Thread-safe pool cleanup in finally block
		/// </summary>
		/// <param name="context">The job execution context containing job ID, type, attributes, and abort flag.</param>
		/// <remarks>
		/// Adapted from monolith source lines 79-158.
		/// Changes: DbContext → CoreDbContext, ErpSettings → IConfiguration, Log → ILogger, JobManager.Settings → IConfiguration.
		/// </remarks>
		public void Process(JobContext context)
		{
			// Create JobDataService using connection string from configuration
			// Replaces monolith's: new JobDataService(JobManager.Settings)
			var connectionString = _configuration["ConnectionStrings:Default"];
			var settings = new JobManagerSettings { DbConnectionString = connectionString };
			JobDataService jobService = new JobDataService(settings);

			Job job = new Job();
			job.Id = context.JobId;
			job.StartedOn = DateTime.UtcNow;
			job.Status = JobStatus.Running;

			try
			{
				jobService.UpdateJob(job);

				lock (_lockObj)
				{
					_pool.Add(context);
				}

				var instance = (ErpJob)Activator.CreateInstance(context.Type.ErpJobType);

				try
				{
					// Replace DbContext.CreateContext(ErpSettings.ConnectionString) with CoreDbContext
					CoreDbContext.CreateContext(connectionString);
					using (var secCtx = SecurityContext.OpenSystemScope())
					{
						// Execute job method
						instance.Execute(context);
					}
				}
				catch (TargetInvocationException ex)
				{
					throw ex.InnerException;
				}
				catch (Exception)
				{
					throw;
				}
				finally
				{
					CoreDbContext.CloseContext();
				}

				if (context.Result != null)
					job.Result = context.Result;

				job.FinishedOn = DateTime.UtcNow;
				job.Status = JobStatus.Finished;
				jobService.UpdateJob(job);
			}
			catch (Exception ex)
			{
				try
				{
					// Replace DbContext.CreateContext(ErpSettings.ConnectionString) with CoreDbContext
					CoreDbContext.CreateContext(connectionString);
					using (var secCtx = SecurityContext.OpenSystemScope())
					{
						// Replace monolith's Log log = new Log(); log.Create(LogType.Error, ...)
						// with structured ILogger
						_logger.LogError(ex, "JobPool.Process.{TypeName} failed", context.Type.Name);

						job.FinishedOn = DateTime.UtcNow;
						job.Status = JobStatus.Failed;
						job.ErrorMessage = ex.Message;
						jobService.UpdateJob(job);
					}
				}
				finally
				{
					CoreDbContext.CloseContext();
				}
			}
			finally
			{
				lock (_lockObj)
				{
					_pool.Remove(context);
				}
			}
		}

		/// <summary>
		/// Signals an active job to abort by setting its <see cref="JobContext.Aborted"/> flag.
		/// The running job must check this flag periodically to honor the abort request.
		/// Thread-safe — pool access is protected by <c>_lockObj</c>.
		/// </summary>
		/// <param name="jobId">The unique identifier of the job to abort.</param>
		/// <remarks>
		/// Preserved exactly from monolith source lines 161-170.
		/// </remarks>
		public void AbortJob(Guid jobId)
		{
			lock (_lockObj)
			{
				var context = _pool.FirstOrDefault(j => j.JobId == jobId);

				if (context != null)
					context.Aborted = true;
			}
		}

		/// <summary>
		/// Checks whether a job of the specified type is currently executing in the pool.
		/// Used to enforce single-instance job type constraints.
		/// Thread-safe — pool access is protected by <c>_lockObj</c>.
		/// </summary>
		/// <param name="typeId">The unique identifier of the job type to check.</param>
		/// <returns><c>true</c> if a job of the specified type is in the pool; otherwise, <c>false</c>.</returns>
		/// <remarks>
		/// Preserved exactly from monolith source lines 172-178.
		/// </remarks>
		public bool HasJobFromTypeInThePool(Guid typeId)
		{
			lock (_lockObj)
			{
				return _pool.Any(c => c.Type.Id == typeId);
			}
		}
	}
}
