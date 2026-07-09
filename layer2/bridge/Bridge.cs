using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace WinDbgAotExt.Bridge
{
	// The debuggee surface exposed to scripts. Wraps the IDebugClient the extension passes in and
	// calls dbgeng by vtable index (verified against dbgeng.h — Execute is index 66, anchored by the
	// known-good Output=14).
	public sealed unsafe class Debugger
	{
		private readonly IntPtr _debugClient;
		public Debugger(IntPtr debugClient) { _debugClient = debugClient; }

		private const uint DEBUG_OUTCTL_THIS_CLIENT = 0x0;
		private const uint DEBUG_EXECUTE_DEFAULT = 0x0;
		private const int QueryInterfaceSlot = 0;
		private const int ReleaseSlot = 2;
		private const int ExecuteSlot = 66;   // IDebugControl::Execute (verified vs dbgeng.h)
		private static readonly Guid IID_IDebugControl = new("5182e668-105e-416e-ad92-24ef800424ba");

		public bool Connected => _debugClient != IntPtr.Zero;

		// Run a WinDbg command in the live target. Output currently goes to the debugger console
		// (capture-and-return is the next slice). Returns the HRESULT from IDebugControl::Execute.
		public int Exec(string command)
		{
			if (_debugClient == IntPtr.Zero) return unchecked((int)0x80004005); // E_FAIL

			nint** clientVtable = *(nint***)_debugClient;
			var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)clientVtable[QueryInterfaceSlot];
			if (queryInterface(_debugClient, IID_IDebugControl, out IntPtr debugControl) != 0 || debugControl == IntPtr.Zero)
				return unchecked((int)0x80004002); // E_NOINTERFACE

			try
			{
				nint** controlVtable = *(nint***)debugControl;
				var execute = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, uint, int>)controlVtable[ExecuteSlot];
				byte[] commandBytes = Encoding.ASCII.GetBytes(command + "\0");
				fixed (byte* commandPointer = commandBytes)
					return execute(debugControl, DEBUG_OUTCTL_THIS_CLIENT, commandPointer, DEBUG_EXECUTE_DEFAULT);
			}
			finally
			{
				nint** controlVtable = *(nint***)debugControl;
				var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)controlVtable[ReleaseSlot];
				release(debugControl);
			}
		}
	}

	public static class Bridge
	{
		// hostfxr's default "component entry point" signature: int F(IntPtr argument, int argumentSizeBytes).
		// Step-1 proof-of-life: return a sentinel so the native host can confirm the call reached managed
		// JIT code and returned.
		public static int Ping(IntPtr argument, int argumentSizeBytes) => 4242;

		// Compile + run live C# via Roslyn INSIDE the hosted CoreCLR — the actual Layer-2 engine.
		// Called via UNMANAGEDCALLERSONLY_METHOD. Takes a UTF-16 code string plus the debugger client
		// (so scripts can reach the live target through `Debugger`), returns a UTF-16 result string
		// allocated with HGlobal (the native caller frees it).
		[UnmanagedCallersOnly]
		public static IntPtr Eval(IntPtr codeUtf16, IntPtr debugClient)
		{
			string sourceCode = Marshal.PtrToStringUni(codeUtf16) ?? "";
			string resultText;
			try
			{
				// Expose `debugger` to the script by baking the client pointer as a literal and letting the
				// SCRIPT construct the Debugger. We deliberately do NOT pass a globals *instance*: the bridge
				// is loaded in one assembly-load-context (hostfxr's IsolatedComponentLoadContext) while Roslyn
				// compiles the script in another, so any Debugger/globals instance created here is a different
				// type identity there (InvalidCastException). A pointer literal crosses cleanly; the instance
				// the script constructs lives entirely inside Roslyn's context.
				string preamble = $"var debugger = new WinDbgAotExt.Bridge.Debugger((System.IntPtr)({debugClient.ToInt64()}L));\n";
				var scriptOptions = ScriptOptions.Default
					.WithReferences(
						typeof(object).Assembly,                          // System.Private.CoreLib / System.Runtime
						typeof(System.Linq.Enumerable).Assembly,          // System.Linq
						typeof(System.Collections.Generic.List<>).Assembly,
						typeof(Debugger).Assembly)                        // this bridge (so `Debugger` resolves)
					.WithImports("System", "System.Linq", "System.Collections.Generic");
				object? resultValue = CSharpScript.EvaluateAsync<object>(preamble + sourceCode, scriptOptions)
					.GetAwaiter().GetResult();
				resultText = resultValue?.ToString() ?? "(null)";
			}
			catch (Exception exception)
			{
				resultText = "ERROR " + exception.GetType().Name + ": " + exception.Message;
			}
			return Marshal.StringToHGlobalUni(resultText);
		}
	}
}
