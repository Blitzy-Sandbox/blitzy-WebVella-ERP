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
				// This is a DELIBERATELY TRUSTED-AUTHOR boundary. The `sourceCode` evaluated here originates
				// EXCLUSIVELY from authenticated administrators/developers authoring server-side snippets and
				// page logic — it is NEVER end-user / request-supplied input. Under that trust model, unsandboxed
				// runtime C# compilation/execution via CSScriptLib is an accepted, documented risk per AAP §0.6.2.
				// DO NOT route untrusted or end-user-controlled input into this method. Any change that would
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
