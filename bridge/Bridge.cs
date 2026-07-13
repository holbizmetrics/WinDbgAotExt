using System;
using System.Runtime.CompilerServices;
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

		// --- Output capture: run a command and return its text, so scripts can parse/LINQ it ---
		private const int GetOutputCallbacksSlot = 33;  // IDebugClient::GetOutputCallbacks (verified vs dbgeng.h)
		private const int SetOutputCallbacksSlot = 34;  // IDebugClient::SetOutputCallbacks
		private const int OutputCallbackSlot = 3;       // IDebugOutputCallbacks::Output
		private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
		private static readonly Guid IID_IDebugOutputCallbacks = new("4bf58045-d654-4c40-b0af-683090f356dc");

		private static readonly StringBuilder _capturedOutput = new();
		private static IntPtr _capturingCallbacks;

		// A managed IDebugOutputCallbacks that appends everything the debugger prints into _capturedOutput.
		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
		private static int CaptureQueryInterface(IntPtr self, Guid* interfaceId, IntPtr* result)
		{
			if (interfaceId != null && (*interfaceId == IID_IDebugOutputCallbacks || *interfaceId == IID_IUnknown))
			{ *result = self; return 0; }
			if (result != null) *result = IntPtr.Zero;
			return unchecked((int)0x80004002); // E_NOINTERFACE
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
		private static uint CaptureAddRefRelease(IntPtr self) => 1;

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
		private static int CaptureOutput(IntPtr self, uint mask, byte* textAnsi)
		{
			if (textAnsi != null) _capturedOutput.Append(Marshal.PtrToStringAnsi((IntPtr)textAnsi));
			return 0;
		}

		// Build our callbacks object once: a native vtable {QI, AddRef, Release, Output} of function pointers.
		private static IntPtr GetCapturingCallbacks()
		{
			if (_capturingCallbacks != IntPtr.Zero) return _capturingCallbacks;
			nint* callbackVtable = (nint*)Marshal.AllocHGlobal(IntPtr.Size * 4);
			callbackVtable[0] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&CaptureQueryInterface;
			callbackVtable[1] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&CaptureAddRefRelease;
			callbackVtable[2] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&CaptureAddRefRelease;
			callbackVtable[OutputCallbackSlot] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)&CaptureOutput;
			nint* callbacksObject = (nint*)Marshal.AllocHGlobal(IntPtr.Size);
			callbacksObject[0] = (nint)callbackVtable;
			_capturingCallbacks = (IntPtr)callbacksObject;
			return _capturingCallbacks;
		}

		public string Run(string command)
		{
			if (_debugClient == IntPtr.Zero) return "(no debug client)";
			nint** clientVtable = *(nint***)_debugClient;
			var getOutputCallbacks = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)clientVtable[GetOutputCallbacksSlot];
			var setOutputCallbacks = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)clientVtable[SetOutputCallbacksSlot];

			getOutputCallbacks(_debugClient, out IntPtr previousCallbacks); // save whatever the debugger was using
			_capturedOutput.Clear();
			setOutputCallbacks(_debugClient, GetCapturingCallbacks());       // install ours
			try { Exec(command); }                                           // output flows to CaptureOutput
			finally { setOutputCallbacks(_debugClient, previousCallbacks); } // always restore
			return _capturedOutput.ToString();
		}

		// --- Memory read: pull raw bytes from the target so scripts can inspect it directly ---
		private const int ReadVirtualSlot = 3;   // IDebugDataSpaces::ReadVirtual (verified vs dbgeng.h)
		private static readonly Guid IID_IDebugDataSpaces = new("88f7dfab-3ea7-4c3a-aefb-c4e8106173aa");

		public byte[] ReadBytes(ulong address, int count)
		{
			if (_debugClient == IntPtr.Zero || count <= 0) return Array.Empty<byte>();
			nint** clientVtable = *(nint***)_debugClient;
			var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)clientVtable[QueryInterfaceSlot];
			if (queryInterface(_debugClient, IID_IDebugDataSpaces, out IntPtr dataSpaces) != 0 || dataSpaces == IntPtr.Zero)
				return Array.Empty<byte>();
			try
			{
				nint** dataSpacesVtable = *(nint***)dataSpaces;
				var readVirtual = (delegate* unmanaged[Stdcall]<IntPtr, ulong, void*, uint, out uint, int>)dataSpacesVtable[ReadVirtualSlot];
				byte[] buffer = new byte[count];
				int hresult;
				uint bytesRead;
				fixed (byte* bufferPointer = buffer)
					hresult = readVirtual(dataSpaces, address, bufferPointer, (uint)count, out bytesRead);
				if (hresult != 0) return Array.Empty<byte>();
				if (bytesRead < (uint)count) Array.Resize(ref buffer, (int)bytesRead);
				return buffer;
			}
			finally
			{
				nint** dataSpacesVtable = *(nint***)dataSpaces;
				var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)dataSpacesVtable[ReleaseSlot];
				release(dataSpaces);
			}
		}

		public ulong ReadU64(ulong address)
		{
			byte[] bytes = ReadBytes(address, 8);
			return bytes.Length == 8 ? BitConverter.ToUInt64(bytes, 0) : 0;
		}

		public uint ReadU32(ulong address)
		{
			byte[] bytes = ReadBytes(address, 4);
			return bytes.Length == 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
		}

		// --- Typed last event: retires `.lastevent` string-sniffing for code + chance ---
		// Slots verified against dbgeng.h 10.0.26100.0, anchored by the two already-proven slots
		// (Output=14, Execute=66): GetDebuggeeType=34, GetLastEventInformation=94 (the interface's
		// last method). Buffer decoding lives in LastEventInfo.Decode (pure, unit-tested).
		private const int GetDebuggeeTypeSlot = 34;          // IDebugControl::GetDebuggeeType
		private const int GetLastEventInformationSlot = 94;  // IDebugControl::GetLastEventInformation
		private const uint DEBUG_DUMP_SMALL = 1024;          // lowest dump qualifier; >= means dump target

		// True when the target is a dump file rather than a live process (GetDebuggeeType
		// qualifier >= DEBUG_DUMP_SMALL); false when live; NULL when the query itself failed.
		// The null case is load-bearing -- callers must NOT collapse "couldn't tell" into "live",
		// or a dump whose stored FirstChance is 0 gets read as a real 2nd-chance fault, resurrecting
		// on the error path the exact false positive the typed path exists to kill. LastEventInfo.Chance
		// funnels null to "unknown".
		public bool? IsDumpTarget
		{
			get
			{
				if (_debugClient == IntPtr.Zero) return null;
				nint** clientVtable = *(nint***)_debugClient;
				var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)clientVtable[QueryInterfaceSlot];
				if (queryInterface(_debugClient, IID_IDebugControl, out IntPtr debugControl) != 0 || debugControl == IntPtr.Zero)
					return null;
				try
				{
					nint** controlVtable = *(nint***)debugControl;
					var getDebuggeeType = (delegate* unmanaged[Stdcall]<IntPtr, out uint, out uint, int>)controlVtable[GetDebuggeeTypeSlot];
					if (getDebuggeeType(debugControl, out uint targetClass, out uint qualifier) != 0) return null;
					return qualifier >= DEBUG_DUMP_SMALL;
				}
				finally { ReleaseInterface(debugControl); }
			}
		}

		// The debugger's last event as a typed object (IDebugControl::GetLastEventInformation).
		// Null when there is no client or the call fails -- callers fall back to `.lastevent` text.
		public LastEventInfo? LastEvent
		{
			get
			{
				if (_debugClient == IntPtr.Zero) return null;
				nint** clientVtable = *(nint***)_debugClient;
				var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)clientVtable[QueryInterfaceSlot];
				if (queryInterface(_debugClient, IID_IDebugControl, out IntPtr debugControl) != 0 || debugControl == IntPtr.Zero)
					return null;
				try
				{
					nint** controlVtable = *(nint***)debugControl;
					var getLastEventInformation = (delegate* unmanaged[Stdcall]<
						IntPtr, out uint, out uint, out uint, byte*, uint, out uint, byte*, uint, out uint, int>)
						controlVtable[GetLastEventInformationSlot];
					const int extraInformationCapacity = 256; // DEBUG_LAST_EVENT_INFO_EXCEPTION is 156 bytes
					const int descriptionCapacity = 512;
					byte* extraInformation = stackalloc byte[extraInformationCapacity];
					byte* description = stackalloc byte[descriptionCapacity];
					int hresult = getLastEventInformation(debugControl,
						out uint eventType, out uint processId, out uint threadId,
						extraInformation, extraInformationCapacity, out uint extraInformationUsed,
						description, descriptionCapacity, out uint descriptionUsed);
					if (hresult < 0) return null; // S_FALSE (1) = truncated but valid; only fail on errors
					int extraInformationLength = (int)Math.Min(extraInformationUsed, extraInformationCapacity);
					string descriptionText = Marshal.PtrToStringAnsi((IntPtr)description) ?? "";
					return LastEventInfo.Decode(eventType, processId, threadId, descriptionText,
						new ReadOnlySpan<byte>(extraInformation, extraInformationLength), IsDumpTarget);
				}
				finally { ReleaseInterface(debugControl); }
			}
		}

		private static void ReleaseInterface(IntPtr interfacePointer)
		{
			nint** vtable = *(nint***)interfacePointer;
			var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[ReleaseSlot];
			release(interfacePointer);
		}

		// --- Queryable state: loaded modules as typed objects, LINQ-able ---
		// (Parses `lm` output for now — honest and dependency-free; a future slice can back this with
		//  IDebugSymbols for robustness. Runs `lm` on each access.)
		public System.Collections.Generic.List<ModuleInfo> Modules
		{
			get
			{
				var modules = new System.Collections.Generic.List<ModuleInfo>();
				foreach (string line in Run("lm").Split('\n'))
				{
					string[] fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
					if (fields.Length < 3) continue; // "<start> <end> <name> ..."; addresses use ` as a digit separator
					if (!ulong.TryParse(fields[0].Replace("`", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong start)) continue;
					if (!ulong.TryParse(fields[1].Replace("`", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong end)) continue;
					modules.Add(new ModuleInfo { Name = fields[2], Start = start, End = end });
				}
				return modules;
			}
		}

		// --- Managed heap as typed objects (ClrMD), the "query the debuggee like a database" pillar ---
		// Unlike Modules (parsed from `lm` text), this walks the target's real GC heap via ClrMD
		// (Microsoft.Diagnostics.Runtime), which attaches to the SAME dbgeng session we're already in
		// through DataTarget.CreateFromDbgEng(_debugClient). Only meaningful when the debuggee is a
		// managed (.NET) process — a native target has no CLR heap (Heap.ClrPresent == false).
		//
		// All ClrMD work stays inside the bridge here; scripts only ever see our own HeapObjectInfo POCO
		// (defined in this assembly, which Eval already references), so ClrMD never has to load in
		// Roslyn's script load-context — sidestepping the Layer-2c cross-ALC type-identity trap.
		public HeapView Heap => new HeapView(_debugClient);
	}

	// A view over the debuggee's managed GC heap. `.Objects` materializes the walk into plain POCOs so
	// the caller can LINQ them freely (GroupBy TypeName for a leak-hunt, etc.) without any ClrMD type
	// crossing back into the script's world.
	public sealed class HeapView
	{
		private readonly IntPtr _debugClient;
		public HeapView(IntPtr debugClient) { _debugClient = debugClient; }

		// True once a walk found a CLR in the target. Distinguishes "empty heap" from "not a .NET process".
		public bool ClrPresent { get; private set; }

		public System.Collections.Generic.List<HeapObjectInfo> Objects
		{
			get
			{
				var heapObjects = new System.Collections.Generic.List<HeapObjectInfo>();
				if (_debugClient == IntPtr.Zero) return heapObjects;

				// CreateFromDbgEng wraps our existing IDebugClient rather than attaching fresh — this is the
				// entry point ClrMD keeps specifically for WinDbg extensions. Disposed here so its dbgeng
				// wrapper is torn down while the debuggee itself keeps running.
				using var dataTarget = Microsoft.Diagnostics.Runtime.DataTarget.CreateFromDbgEng(_debugClient);
				if (dataTarget.ClrVersions.Length == 0) return heapObjects; // native target: no managed heap
				ClrPresent = true;

				using var clrRuntime = dataTarget.ClrVersions[0].CreateRuntime();
				foreach (var clrObject in clrRuntime.Heap.EnumerateObjects())
				{
					heapObjects.Add(new HeapObjectInfo
					{
						Address = clrObject.Address,
						Size = clrObject.Size,
						TypeName = clrObject.Type?.Name ?? "<unknown>",
					});
				}
				return heapObjects;
			}
		}
	}

	// One managed object on the debuggee's heap, projected to plain fields for LINQ.
	public sealed class HeapObjectInfo
	{
		public ulong Address { get; init; }
		public ulong Size { get; init; }
		public string TypeName { get; init; } = "";
		public override string ToString() => $"{TypeName} @ 0x{Address:x} (0x{Size:x} bytes)";
	}

	// A loaded module, parsed from `lm` — enough to LINQ (name, address range, size).
	public sealed class ModuleInfo
	{
		public string Name { get; init; } = "";
		public ulong Start { get; init; }
		public ulong End { get; init; }
		public ulong Size => End - Start;
		public override string ToString() => $"{Name} @ 0x{Start:x} (0x{Size:x} bytes)";
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
