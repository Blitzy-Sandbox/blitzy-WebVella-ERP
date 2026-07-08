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

				// SECURITY (A03 / CWE-94, ACCEPTED RISK): this compiles & executes admin-authored C# at runtime (CS-Script).
				// It is a deliberate, trusted-author feature (code data sources, page-component code, snippets) and must NOT
				// receive untrusted input. The only request path that submits arbitrary source code for compilation is the
				// code-compile API in WebVella.Erp.Web.Controllers.WebApiController (route api/v3.0/datasource/code-compile),
				// which now carries [Authorize(Roles = "administrator")] in addition to the controller's class-level [Authorize];
				// arbitrary compilation is therefore reachable only by administrators, not by every authenticated user. The
				// runtime Evaluate path executes code that was already persisted (code data sources / page-component code) via
				// the admin-only page-builder / SDK tooling. Documented as accepted-risk in SECURITY.md.
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
