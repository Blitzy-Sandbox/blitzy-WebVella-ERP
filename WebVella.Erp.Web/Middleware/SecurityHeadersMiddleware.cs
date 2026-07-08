using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;

namespace WebVella.Erp.Web.Middleware
{
	// SECURITY (A05 / CWE-693, CWE-1021, CWE-16): emit a baseline set of hardening response headers on every
	// response to mitigate MIME-sniffing (X-Content-Type-Options), clickjacking (X-Frame-Options — CWE-1021),
	// referrer leakage (Referrer-Policy), and powerful-feature abuse (Permissions-Policy). X-XSS-Protection is
	// intentionally "0" (the legacy XSS auditor is itself buggy/risky per modern guidance).
	public class SecurityHeadersMiddleware
	{
		RequestDelegate next;

		public SecurityHeadersMiddleware(RequestDelegate next)
		{
			this.next = next;
		}

		public async Task InvokeAsync(HttpContext context)
		{
			var headers = context.Response.Headers;
			// Indexer assignment (not .Add) so a pre-existing header cannot cause a duplicate-key exception.
			headers["X-Content-Type-Options"] = "nosniff";
			headers["X-Frame-Options"] = "DENY";
			headers["X-XSS-Protection"] = "0"; // intentionally disabled: the legacy browser XSS auditor is itself risky
			headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
			headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
			// CSP is emitted REPORT-ONLY first because the WebVella UI relies on inline scripts/styles (Bootstrap,
			// jQuery, Web-Components). Promote to the enforcing "Content-Security-Policy" header (optionally with
			// nonces/hashes) only after the report-only telemetry is clean.
			headers["Content-Security-Policy-Report-Only"] = "default-src 'self'";
			// NOTE: Strict-Transport-Security is intentionally NOT set here; each host adds it via UseHsts() (production only).
			await next(context);
		}
	}

	public static class SecurityHeadersMiddlewareExtensions
	{
		public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
		{
			app.UseMiddleware<SecurityHeadersMiddleware>();
			return app;
		}
	}
}
