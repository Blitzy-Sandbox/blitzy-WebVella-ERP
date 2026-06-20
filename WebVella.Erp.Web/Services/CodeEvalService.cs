using CSScriptLib;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using WebVella.Erp.Web.Models;

namespace WebVella.Erp.Web.Service
{
	public static class CodeEvalService
	{
		private static object lockObj = new object();
		private static readonly Dictionary<string, object> scriptObjects = new Dictionary<string, object>();

		//private static string CalculateMD5Hash(string input)
		//{
		//	MD5 md5 = MD5.Create();
		//	byte[] inputBytes = Encoding.ASCII.GetBytes(input);
		//	byte[] hash = md5.ComputeHash(inputBytes);

		//	StringBuilder sb = new StringBuilder();
		//	for (int i = 0; i < hash.Length; i++)
		//		sb.Append(hash[i].ToString("X2"));
		//	return sb.ToString();
		//}

		private static ICodeVariable GetScriptObject(string sourceCode)
		{
			if (string.IsNullOrWhiteSpace(sourceCode))
				throw new ArgumentException("SourceCode is empty");

			string md5Key = sourceCode;
			if (scriptObjects.ContainsKey(md5Key))
				return scriptObjects[md5Key] as ICodeVariable;

			lock (lockObj)
			{

				//dublication of MD5 hash, so we stopped using it
				//string md5Key = CalculateMD5Hash(sourceCode);
				if (scriptObjects.ContainsKey(md5Key))
					return scriptObjects[md5Key] as ICodeVariable;

				// SECURITY (A03 Injection — documented, accepted Medium risk; CWE-94 Code Injection / CWE-95 Eval Injection):
				// This is a DELIBERATELY TRUSTED-AUTHOR boundary, and that boundary is ENFORCED at every entry
				// point into this service (it is NOT merely assumed). The `sourceCode` evaluated here reaches
				// GetScriptObject only via the following trusted-author paths:
				//   * CodeEvalService.Compile(...) — invoked solely by WebApiController's "api/v3.0/datasource/code-compile"
				//     endpoint, which is gated with [Authorize(Roles = "administrator")]. Class-level [Authorize]
				//     alone is NOT sufficient (it permits any authenticated user); the explicit administrator role
				//     requirement is what enforces the trusted-author boundary for request-driven compilation.
				//   * CodeEvalService.Evaluate(...) — invoked at page-render time over server-side code that was
				//     authored at DESIGN time by administrators: DataSource CODE variables and .cs snippets persisted
				//     through the administrator-only page/snippet designer (see PageDataModel). This persisted metadata
				//     is never request-supplied; the trust boundary is the administrator authoring permission.
				// Under that enforced trust model, unsandboxed runtime C# compilation/execution via CSScriptLib is an
				// accepted, documented risk per AAP §0.6.2. DO NOT route untrusted or end-user-controlled input into
				// this method, and DO NOT add a caller that is not restricted to administrators. Any change that would
				// accept untrusted input here MUST be escalated and the evaluation MUST be sandboxed/isolated first.
				CSScript.EvaluatorConfig.ReferenceDomainAssemblies = true;
				ICodeVariable scriptObject = CSScript.Evaluator.LoadCode<ICodeVariable>(sourceCode);
				scriptObjects[md5Key] = scriptObject;
				return scriptObject;
			}
		}

		public static object Evaluate(string sourceCode, BaseErpPageModel pageModel)
		{
			ICodeVariable script = GetScriptObject(sourceCode);
			return script.Evaluate(pageModel);
		}

		internal static void Compile(string sourceCode)
		{
			ICodeVariable script = GetScriptObject(sourceCode);
		}
	}
}
