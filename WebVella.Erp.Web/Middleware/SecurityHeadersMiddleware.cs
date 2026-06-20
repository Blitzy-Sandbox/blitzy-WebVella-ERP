using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WebVella.Erp.Web.Middleware
{
	public class SecurityHeadersMiddleware
	{
		// Default Content-Security-Policy (verbatim from the prompt / AAP §0.7.3); used as the safe
		// default when no override is supplied via configuration.
		private const string DefaultContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'";

		// Default posture: Report-Only is ON so the strict Content-Security-Policy cannot break existing
		// inline Razor/Stencil scripts, inline styles, or vendored client libraries (jQuery/Select2/
		// Chart.js/toastr) on first deployment. This is the functional-parity safeguard the AAP explicitly
		// mandates (§0.3.4 / §0.6.3): "the CSP is ... deployable in Content-Security-Policy-Report-Only
		// mode first, then tightened." The header is therefore emitted as "Content-Security-Policy-Report-Only"
		// by default, so violations are REPORTED but NOT enforced/blocked. Operators opt into hard enforcement
		// (the "Content-Security-Policy" header) by setting Settings:SecurityHeaders:ContentSecurityPolicyReportOnly
		// to false in configuration — no code change required. The exact policy STRING is identical in either
		// mode, so the char-for-char policy value (AAP §0.7.3 / User Example 1) is preserved regardless.
		private const bool DefaultContentSecurityPolicyReportOnly = true;

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
				//X-Frame-Options is set UNCONDITIONALLY (overwrite), unlike the deferential headers around it.
				//The ASP.NET Core Razor render path emits "X-Frame-Options: SAMEORIGIN" on Razor-page responses
				//(e.g. /login), and a deferential SetHeaderIfMissing cannot replace it — leaving the AAP-mandated
				//exact value "DENY" (§0.7.3) unmet on those responses. Because this middleware is registered at the
				//FRONT of every host pipeline (via UseErpSecurityHeaders), its OnStarting callback is the FIRST
				//registered and therefore the LAST to run (ASP.NET Core fires OnStarting callbacks LIFO), so this
				//assignment runs after any framework-set value and deterministically wins. Indexer assignment
				//replaces the value in place, so the header is still emitted exactly once (no duplication).
				SetHeaderOverwrite(headers, "X-Frame-Options", "DENY");
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

		//Sets the header to the exact required value, REPLACING any value a downstream/framework component may
		//have already set. Used for X-Frame-Options, where the framework's Razor render path otherwise emits
		//SAMEORIGIN and the AAP (§0.7.3) requires DENY. Indexer assignment replaces in place (single value),
		//so the header is emitted exactly once (no duplicate-key emission).
		private static void SetHeaderOverwrite(IHeaderDictionary headers, string name, string value)
		{
			headers[name] = value;
		}
	}
}
