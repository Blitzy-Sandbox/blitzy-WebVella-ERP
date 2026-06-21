using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebVella.Erp.Site.MicrosoftCDM
{
	public class Program
	{
		public static void Main(string[] args)
		{
			BuildWebHost(args).Run();
		}

		public static IWebHost BuildWebHost(string[] args) =>
		   WebHost.CreateDefaultBuilder(args)
			   // QA Issue 1 (static web assets): force RCL _content/* assets (WebVella.Erp.Web, WebVella.TagHelpers) to load in EVERY environment - WebHost.CreateDefaultBuilder only auto-loads them in Development, so a Release host run outside Development returned 405 for all static assets and broke UI parity.
			   .UseStaticWebAssets()
			   .UseStartup<Startup>()
			   .Build();
	}
}
