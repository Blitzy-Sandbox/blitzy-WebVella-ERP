namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Abstract base class for all background jobs in the Core Platform service.
	/// Subclasses override <see cref="Execute"/> to implement job logic.
	/// Preserved exactly from monolith <c>WebVella.Erp.Jobs.ErpJob</c>.
	/// </summary>
	public abstract class ErpJob
	{
		/// <summary>
		/// Executes the job logic. Override in subclasses to implement specific job behavior.
		/// The <paramref name="context"/> provides job attributes, abort signaling, and result storage.
		/// </summary>
		/// <param name="context">The job execution context.</param>
		public virtual void Execute(JobContext context)
		{
		}
	}
}
