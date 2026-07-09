using System;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace WinDbgAotExt.Bridge
{
	public static class Bridge
	{
		// Step 1 sanity: proves the native -> CoreCLR -> managed round-trip (default component
		// entry-point signature: int F(IntPtr arg, int argSizeBytes)).
		public static int Ping(IntPtr arg, int argSizeBytes) => 4242;

		// Step 2: compile + run live C# via Roslyn INSIDE the hosted CoreCLR — the actual Layer-2
		// engine. Called via UNMANAGEDCALLERSONLY_METHOD. Takes a UTF-16 code string, returns a
		// UTF-16 result string allocated with HGlobal (the native caller frees it).
		// (Step 2b swaps CSharpScript for the operator's EvaluatorLib once net-versions align.)
		[UnmanagedCallersOnly]
		public static IntPtr Eval(IntPtr codeUtf16)
		{
			string code = Marshal.PtrToStringUni(codeUtf16) ?? "";
			string result;
			try
			{
				var opts = ScriptOptions.Default
					.WithReferences(
						typeof(object).Assembly,                          // System.Private.CoreLib / System.Runtime
						typeof(System.Linq.Enumerable).Assembly,          // System.Linq
						typeof(System.Collections.Generic.List<>).Assembly)
					.WithImports("System", "System.Linq", "System.Collections.Generic");
				object? val = CSharpScript.EvaluateAsync<object>(code, opts).GetAwaiter().GetResult();
				result = val?.ToString() ?? "(null)";
			}
			catch (Exception e) { result = "ERROR " + e.GetType().Name + ": " + e.Message; }
			return Marshal.StringToHGlobalUni(result);
		}
	}
}
