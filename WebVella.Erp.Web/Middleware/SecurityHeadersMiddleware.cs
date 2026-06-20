using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WebVella.Erp.Web.Middleware
{
	public class SecurityHeadersMiddleware
	{
		// Default Content-Security-Policy (verbatim from the prompt / AAP §0.7.3); used as the safe
		// default when no override is supplied via configuration.
		private const string DefaultContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'";

		// Default posture: the strict Content-Security-Policy is ENFORCED by default (emitted as the
		// "Content-Security-Policy" header with the exact literal above), satisfying the char-for-char
		// security-header requirement (AAP §0.7.3 / User Example 1). For a staged rollout where the strict
		// policy might otherwise break existing inline Razor/Stencil scripts or vendored client libraries,
		// operators can opt into Report-Only mode via configuration (set the flag true) without any code
		// change — both the policy string and the report-only toggle remain configurable below.
		private const bool DefaultContentSecurityPolicyReportOnly = false;

		RequestDelegate next;

		public SecurityHeadersMiddleware(RequestDelegate next)
		{
			this.next = next;
		}

		public async Task Invoke(HttpContext context)
		{
			//Register the headers just before the response starts flushing. OnStarting is the correct hook
			//because downstream components may have already begun composing the response.
			context.Response.OnStarting(() =>
			{
				var headers = context.Response.Headers;

				SetHeaderIfMissing(headers, "X-Content-Type-Options", "nosniff");
				SetHeaderIfMissing(headers, "X-Frame-Options", "DENY");
				SetHeaderIfMissing(headers, "X-XSS-Protection", "0");
				SetHeaderIfMissing(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
				SetHeaderIfMissing(headers, "Permissions-Policy", "geolocation=(), microphone=(), camera=()");
				SetHeaderIfMissing(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");

				//CSP is configurable (policy string + report-only toggle) so it can be tuned/tightened without
				//a code change. Defaults preserve the exact literal policy above.
				string policy = DefaultContentSecurityPolicy;
				bool reportOnly = DefaultContentSecurityPolicyReportOnly;

				var configuration = ErpSettings.Configuration;
				if (configuration != null)
				{
					var configuredPolicy = configuration["Settings:SecurityHeaders:ContentSecurityPolicy"];
					if (!string.IsNullOrWhiteSpace(configuredPolicy))
						policy = configuredPolicy;

					var configuredReportOnly = configuration["Settings:SecurityHeaders:ContentSecurityPolicyReportOnly"];
					if (!string.IsNullOrWhiteSpace(configuredReportOnly))
						bool.TryParse(configuredReportOnly, out reportOnly);
				}

				string cspHeaderName = reportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy";
				SetHeaderIfMissing(headers, cspHeaderName, policy);

				return Task.CompletedTask;
			});

			await next(context);
		}

		private static void SetHeaderIfMissing(IHeaderDictionary headers, string name, string value)
		{
			if (!headers.ContainsKey(name))
				headers[name] = value;
		}
	}
}
