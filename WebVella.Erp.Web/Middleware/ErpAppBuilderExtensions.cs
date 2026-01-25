using Microsoft.AspNetCore.Builder;

namespace WebVella.Erp.Web.Middleware
{
	public static class AppBuilderExtensions
	{
		public static IApplicationBuilder UseErpMiddleware(this IApplicationBuilder app)
		{
			app.UseMiddleware<ErpMiddleware>();
			return app;
		}

		public static IApplicationBuilder UseJwtMiddleware(this IApplicationBuilder app)
		{
			app.UseMiddleware<JwtMiddleware>();
			return app;
		}

		public static IApplicationBuilder UseDebugLogMiddleware(this IApplicationBuilder app)
		{
			app.UseMiddleware<ErpDebugLogMiddleware>();
			return app;
		}

		public static IApplicationBuilder UseErrorHandlingMiddleware(this IApplicationBuilder app)
		{
			app.UseMiddleware<ErpErrorHandlingMiddleware>();
			return app;
		}

		/// <summary>
		/// Adds the OWASP security headers middleware to the application pipeline.
		/// This middleware adds protective HTTP headers (X-Frame-Options, X-Content-Type-Options,
		/// Content-Security-Policy, Strict-Transport-Security, Referrer-Policy, Permissions-Policy)
		/// to all responses for CWE-693 mitigation.
		/// </summary>
		/// <param name="app">The application builder.</param>
		/// <returns>The application builder for method chaining.</returns>
		public static IApplicationBuilder UseSecurityHeadersMiddleware(this IApplicationBuilder app)
		{
			app.UseMiddleware<SecurityHeadersMiddleware>();
			return app;
		}
	}
}
