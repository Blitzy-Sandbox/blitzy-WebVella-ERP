using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WebVella.Erp.Web.Middleware
{
	public class SecurityHeadersMiddleware
	{
		// Default Content-Security-Policy (verbatim from the prompt / AAP §0.7.3); used as the safe
		// default when no override is supplied via configuration.
		private const string DefaultContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'";

		// Default posture: Report-Only is ON by default so the strict CSP cannot break existing inline
		// Razor/Stencil scripts/styles or vendored client libraries on first deployment. Per AAP §0.3.4 /
		// §0.6.3 the CSP is deployed in Report-Only mode FIRST (for functional parity), then tightened. In
		// this mode the response carries the header name "Content-Security-Policy-Report-Only" with the
		// verbatim policy above — the CSP VALUE is unchanged (User Example 1 / AAP §0.7.3); only the header
		// name differs from the enforcing variant, so violations are REPORTED, not enforced. Operators switch
		// to enforce mode (header "Content-Security-Policy") by setting
		// "Settings:SecurityHeaders:ContentSecurityPolicyReportOnly" = false in configuration once inline
		// scripts/styles have been tuned — no code change required.
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
				//SECURITY (OWASP A05 - CWE-1021 Clickjacking): force the EXACT value "DENY", OVERWRITING any value an
				//earlier component may have written. ASP.NET Core Razor Pages emits "X-Frame-Options: SAMEORIGIN" by
				//default on page (e.g. /login GET) responses; that value is written before this middleware's OnStarting
				//callback runs, so a missing-only check (SetHeaderIfMissing) would leave the weaker SAMEORIGIN in place
				//on those endpoints (verified inconsistency: API/JWT/protected responses returned DENY but /login GET
				//returned SAMEORIGIN). An unconditional set guarantees the prompt's exact value
				//(User Example 1 / AAP §0.7.3) "X-Frame-Options: DENY" on EVERY response/endpoint uniformly.
				SetHeader(headers, "X-Frame-Options", "DENY");
				SetHeaderIfMissing(headers, "X-XSS-Protection", "0");
				SetHeaderIfMissing(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
				SetHeaderIfMissing(headers, "Permissions-Policy", "geolocation=(), microphone=(), camera=()");
				//SECURITY (OWASP A05 - CWE-1021/CWE-693): force the EXACT HSTS value, OVERWRITING any value an earlier
				//ASP.NET Core UseHsts() call may have written. UseHsts() (active on most hosts) emits a 30-day max-age
				//WITHOUT includeSubDomains by default and runs before this middleware, so a missing-only check would
				//leave that weaker value in place. An unconditional set guarantees the prompt's exact value
				//(User Example 1 / AAP §0.7.3) on every response. The matching AddHsts(...) configured per host makes
				//the UseHsts() layer emit the same value; this central overwrite is the authoritative guarantee.
				SetHeader(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");

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
					//SECURITY/robustness (functional parity, AAP §0.3.4 / §0.6.3): only override the safe report-only
					//default when the configured value parses successfully. bool.TryParse writes false into its out
					//parameter on a failed parse, so assigning it unconditionally would let a present-but-invalid value
					//silently flip CSP from report-only to enforced and risk breaking existing inline scripts/styles.
					if (!string.IsNullOrWhiteSpace(configuredReportOnly) && bool.TryParse(configuredReportOnly, out bool parsedReportOnly))
						reportOnly = parsedReportOnly;
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

		//Force-set a header to an exact value, overwriting any value a prior middleware (e.g. UseHsts) emitted.
		private static void SetHeader(IHeaderDictionary headers, string name, string value)
		{
			headers[name] = value;
		}
	}
}
