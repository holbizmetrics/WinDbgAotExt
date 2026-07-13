#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt;

// LAYER 2 inside the extension: boots CoreCLR in-process (via hostfxr) and calls the managed Bridge,
// so a WinDbg command can run live C# through Roslyn. The extension is NativeAOT (no CoreCLR of its
// own), so our hostfxr_initialize_for_runtime_config is the FIRST init in the debugger's process —
// which is exactly why this works (proven in the host/ spike; this ports it into the .load'able DLL).
internal static unsafe class ClrHost
{
	private const int RuntimeDelegateLoadAssemblyAndGetFunctionPointer = 5;
	private const int GetModuleHandleExFlagFromAddress = 0x4;
	private const int GetModuleHandleExFlagUnchangedRefcount = 0x2;

	private static bool _isBooted;
	private static string? _bridgeDllPath;
	private static delegate* unmanaged<char*, char*, char*, char*, void*, void**, int> _loadAssemblyAndGetFunctionPointer;

	// A method whose address lies inside THIS DLL — used to locate our own module path.
	[UnmanagedCallersOnly] private static void ModuleAnchor() { }

	// Boots the runtime once (cached). Returns null on success, else a human-readable error string.
	public static string? EnsureBooted()
	{
		if (_isBooted) return null;
		try
		{
			string extensionDirectory = GetOwnDirectory();
			string bridgeDirectory = Path.Combine(extensionDirectory, "bridge");
			_bridgeDllPath = Path.Combine(bridgeDirectory, "WinDbgAotExt.Bridge.dll");
			string runtimeConfigPath = Path.Combine(bridgeDirectory, "WinDbgAotExt.Bridge.runtimeconfig.json");
			if (!File.Exists(runtimeConfigPath))
				return "bridge runtimeconfig not found next to extension: " + runtimeConfigPath;

			string hostfxrPath = FindHostFxr();
			IntPtr hostfxrLibrary = NativeLibrary.Load(hostfxrPath);
			var initializeForRuntimeConfig = (delegate* unmanaged<char*, IntPtr, out IntPtr, int>)
				NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_initialize_for_runtime_config");
			var getRuntimeDelegate = (delegate* unmanaged<IntPtr, int, out IntPtr, int>)
				NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_get_runtime_delegate");

			int hresult;
			IntPtr hostContext;
			fixed (char* runtimeConfigPathPointer = runtimeConfigPath)
				hresult = initializeForRuntimeConfig(runtimeConfigPathPointer, IntPtr.Zero, out hostContext);
			if (hresult < 0 || hostContext == IntPtr.Zero) return $"hostfxr init failed 0x{hresult:X8}";

			hresult = getRuntimeDelegate(hostContext, RuntimeDelegateLoadAssemblyAndGetFunctionPointer, out IntPtr loadFunctionPointer);
			if (hresult != 0 || loadFunctionPointer == IntPtr.Zero) return $"get_runtime_delegate failed 0x{hresult:X8}";

			_loadAssemblyAndGetFunctionPointer = (delegate* unmanaged<char*, char*, char*, char*, void*, void**, int>)loadFunctionPointer;
			_isBooted = true;
			return null;
		}
		catch (Exception exception)
		{
			return exception.GetType().Name + ": " + exception.Message;
		}
	}

	// Step-3a de-risk: prove the extension can reach managed CoreCLR code. Returns 4242 on success.
	public static int Ping()
	{
		if (EnsureBooted() != null) return -1;
		IntPtr pingFunctionPointer;
		fixed (char* assemblyPath = _bridgeDllPath)
		fixed (char* typeName = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* methodName = "Ping")
		{
			void* functionPointer;
			int hresult = _loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName, null, null, &functionPointer);
			if (hresult != 0) return -1;
			pingFunctionPointer = (IntPtr)functionPointer;
		}
		var ping = (delegate* unmanaged<IntPtr, int, int>)pingFunctionPointer;
		return ping(IntPtr.Zero, 0);
	}

	// Compile + run live C# via Roslyn in the hosted CoreCLR, handing the script the debugger client
	// so it can reach the live target (Debugger.Exec, ...). Returns the result string.
	public static string Eval(string sourceCode, IntPtr debugClient)
	{
		var error = EnsureBooted();
		if (error != null) return "CLR boot failed: " + error;
		IntPtr evalFunctionPointer;
		fixed (char* assemblyPath = _bridgeDllPath)
		fixed (char* typeName = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* methodName = "Eval")
		{
			void* functionPointer;
			int hresult = _loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName,
				(char*)(nint)(-1), null, &functionPointer); // (char*)-1 = UNMANAGEDCALLERSONLY_METHOD
			if (hresult != 0) return $"load Eval failed 0x{hresult:X8}";
			evalFunctionPointer = (IntPtr)functionPointer;
		}
		var evaluate = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)evalFunctionPointer; // (codeUtf16, debugClient) -> resultUtf16
		IntPtr codePointer = Marshal.StringToHGlobalUni(sourceCode);
		IntPtr resultPointer = evaluate(codePointer, debugClient);
		string result = Marshal.PtrToStringUni(resultPointer) ?? "(null)";
		Marshal.FreeHGlobal(codePointer);
		if (resultPointer != IntPtr.Zero) Marshal.FreeHGlobal(resultPointer);
		return result;
	}

	private static string GetOwnDirectory()
	{
		delegate* unmanaged<void> anchorAddress = &ModuleAnchor;
		if (!GetModuleHandleExW(GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefcount,
				(IntPtr)anchorAddress, out IntPtr moduleHandle))
			throw new InvalidOperationException("GetModuleHandleExW failed");
		char* pathBuffer = stackalloc char[520];
		uint pathLength = GetModuleFileNameW(moduleHandle, pathBuffer, 520);
		string ownDllPath = new string(pathBuffer, 0, (int)pathLength);
		return Path.GetDirectoryName(ownDllPath) ?? throw new InvalidOperationException("no directory for " + ownDllPath);
	}

	private static string FindHostFxr()
	{
		string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
		string frameworkResolverBase = Path.Combine(dotnetRoot, "host", "fxr");
		string? hostfxrPath = Directory.Exists(frameworkResolverBase)
			? Directory.GetDirectories(frameworkResolverBase).OrderBy(directory => directory)
				.Select(directory => Path.Combine(directory, "hostfxr.dll")).LastOrDefault(File.Exists)
			: null;
		return hostfxrPath ?? throw new FileNotFoundException("hostfxr.dll not found under " + frameworkResolverBase);
	}

	[DllImport("kernel32", SetLastError = true)]
	private static extern bool GetModuleHandleExW(int flags, IntPtr address, out IntPtr module);
	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetModuleFileNameW(IntPtr module, char* filename, uint size);
}
