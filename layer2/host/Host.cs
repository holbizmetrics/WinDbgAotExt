using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt.Host;

// Boots CoreCLR in-process via hostfxr and calls the managed Bridge — the make-or-break Layer-2 seam.
// Stands in for the native AOT WinDbg extension so the hosting can be proven without a debugger.
internal static unsafe class Program
{
	private const int RuntimeDelegateLoadAssemblyAndGetFunctionPointer = 5;

	private static int Main(string[] arguments)
	{
		if (arguments.Length < 1)
		{
			Console.Error.WriteLine("usage: Host <bridge-output-directory> [<C# expression> ...]");
			return 2;
		}
		string bridgeDirectory = arguments[0];
		string bridgeDllPath = Path.Combine(bridgeDirectory, "WinDbgAotExt.Bridge.dll");
		string runtimeConfigPath = Path.Combine(bridgeDirectory, "WinDbgAotExt.Bridge.runtimeconfig.json");
		if (!File.Exists(runtimeConfigPath)) { Console.Error.WriteLine("missing " + runtimeConfigPath); return 2; }

		string hostfxrPath = FindHostFxr();
		Console.WriteLine("hostfxr : " + hostfxrPath);
		IntPtr hostfxrLibrary = NativeLibrary.Load(hostfxrPath);

		var initializeForRuntimeConfig = (delegate* unmanaged<char*, IntPtr, out IntPtr, int>)
			NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_initialize_for_runtime_config");
		var getRuntimeDelegate = (delegate* unmanaged<IntPtr, int, out IntPtr, int>)
			NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_get_runtime_delegate");
		var closeHostContext = (delegate* unmanaged<IntPtr, int>)
			NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_close");

		int hresult;
		IntPtr hostContext;
		fixed (char* runtimeConfigPathPointer = runtimeConfigPath)
			hresult = initializeForRuntimeConfig(runtimeConfigPathPointer, IntPtr.Zero, out hostContext);
		// 0=Success, 1=Success_HostAlreadyInitialized, 2=Success_DifferentRuntimeProperties
		if (hresult < 0 || hostContext == IntPtr.Zero) { Console.Error.WriteLine($"initialize failed 0x{hresult:X8}"); return 3; }
		Console.WriteLine($"initialize: hresult=0x{hresult:X8} hostContext=0x{hostContext.ToInt64():X}");

		hresult = getRuntimeDelegate(hostContext, RuntimeDelegateLoadAssemblyAndGetFunctionPointer, out IntPtr loadFunctionPointer);
		if (hresult != 0 || loadFunctionPointer == IntPtr.Zero) { Console.Error.WriteLine($"get_runtime_delegate failed 0x{hresult:X8}"); closeHostContext(hostContext); return 4; }
		var loadAssemblyAndGetFunctionPointer = (delegate* unmanaged<char*, char*, char*, char*, void*, void**, int>)loadFunctionPointer;

		// --- Step 1: call Bridge.Ping (default component-entry-point signature) as a sanity check ---
		IntPtr pingFunctionPointer;
		fixed (char* assemblyPath = bridgeDllPath)
		fixed (char* typeName = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* methodName = "Ping")
		{
			void* functionPointer;
			hresult = loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName, null, null, &functionPointer);
			pingFunctionPointer = (IntPtr)functionPointer;
		}
		if (hresult != 0 || pingFunctionPointer == IntPtr.Zero) { Console.Error.WriteLine($"load Ping failed 0x{hresult:X8}"); closeHostContext(hostContext); return 5; }
		var ping = (delegate* unmanaged<IntPtr, int, int>)pingFunctionPointer;
		int pingResult = ping(IntPtr.Zero, 0);
		Console.WriteLine($"Ping returned: {pingResult}  (expected 4242)");

		// --- Step 2: get Eval (an [UnmanagedCallersOnly] method) and run live C# through Roslyn ---
		// delegate_type_name = UNMANAGEDCALLERSONLY_METHOD = (char*)-1 → return the method's own pointer.
		IntPtr evalFunctionPointer;
		fixed (char* assemblyPath = bridgeDllPath)
		fixed (char* typeName = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* methodName = "Eval")
		{
			void* functionPointer;
			hresult = loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName, (char*)(nint)(-1), null, &functionPointer);
			evalFunctionPointer = (IntPtr)functionPointer;
		}
		if (hresult != 0 || evalFunctionPointer == IntPtr.Zero) { Console.Error.WriteLine($"get Eval failed 0x{hresult:X8}"); closeHostContext(hostContext); return 6; }
		var evaluate = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)evalFunctionPointer;

		// The standalone host has no debugger, so pass IntPtr.Zero as the debug client.
		string[] expressions = arguments.Length > 1
			? arguments[1..]
			: new[] { "1 + 2", "Enumerable.Range(1,10).Where(number => number % 2 == 0).Sum()" };
		foreach (string expression in expressions)
		{
			IntPtr codePointer = Marshal.StringToHGlobalUni(expression);
			IntPtr resultPointer = evaluate(codePointer, IntPtr.Zero);
			string result = Marshal.PtrToStringUni(resultPointer) ?? "(null)";
			Console.WriteLine($"  eval(\"{expression}\") = {result}");
			Marshal.FreeHGlobal(codePointer);
			Marshal.FreeHGlobal(resultPointer);
		}

		closeHostContext(hostContext);
		return pingResult == 4242 ? 0 : 1;
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
}
