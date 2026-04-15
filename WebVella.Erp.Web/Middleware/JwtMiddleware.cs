using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System;
using Microsoft.AspNetCore.Authentication;
using System.Linq;
using System.Security.Claims;
using WebVella.Erp.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace WebVella.Erp.Web.Middleware
{
	public class JwtMiddleware
	{
		private readonly RequestDelegate _next;
		private readonly ILogger<JwtMiddleware> _logger;

		public JwtMiddleware(RequestDelegate next, ILogger<JwtMiddleware> logger)
		{
			_next = next;
			_logger = logger;
		}

		public async Task Invoke(HttpContext context)
		{
			var token = await context.GetTokenAsync("access_token");
			if (string.IsNullOrWhiteSpace(token))
			{
				token = context.Request.Headers[HeaderNames.Authorization];
				if (!string.IsNullOrWhiteSpace(token))
				{
					if (token.Length <= 7)
						token = null;
					else
						token = token.Substring(7);
				}
				else
					token = null;
			}

			if (token != null)
			{
				try
				{
					var jwtToken = await WebVella.Erp.Web.Services.AuthService.GetValidSecurityTokenAsync(token);
					if (jwtToken != null && jwtToken.Claims.Any())
					{
						var nameIdentifier = jwtToken.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier).Value;
						if (!string.IsNullOrWhiteSpace(nameIdentifier))
						{
							var user = new SecurityManager().GetUser(new Guid(nameIdentifier));
							context.Items["User"] = user;

						   var identity = new ClaimsIdentity(jwtToken.Claims, "jwt");
						   context.User = new ClaimsPrincipal(identity);
						}
					}
				}
				catch (Exception ex)
				{
					// SECURITY FIX: Log JWT validation failures (CWE-778 mitigation)
					_logger.LogWarning(ex, "[Security] JWT validation failed for request to {Path}", context.Request.Path);
					// user is not attached to context so request won't have access to secure routes
				}

			}

			await _next(context);
		}
	}
}
