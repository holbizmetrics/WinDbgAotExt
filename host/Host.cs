using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt.Host;

/// <summary>
/// Boots CoreCLR in-process via hostfxr and calls the managed Bridge — the make-or-break Layer-2
/// seam. Stands in for the native AOT WinDbg extension so the hosting can be proven without a
/// debugger.
/// </summary>
internal static unsafe class Program
{
	private const int RuntimeDelegateLoadAssemblyAndGetFunctionPointer = 5;

	private static int Main(string[] arguments)
	{
		if (arguments.Length < 1)
		{
			Console.Error.WriteLine("usage: Host <bridge-output-directory> [<C# expression> ...]");
			Console.Error.WriteLine("       Host <bridge-output-directory> --probe-double-init");
			return 2;
		}
		string bridgeDirectory = arguments[0];
		if (arguments.Length > 1 && arguments[1] == "--probe-double-init")
			return ProbeDoubleInit(bridgeDirectory);
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

	/// <summary>
	/// Diagnostic probe for the Layer-2 precondition: initialize a runtime TWICE in one process and
	/// print both results. The design rests on "our hostfxr init is the FIRST init in this process";
	/// this measures what hostfxr actually returns when it is not — the case that produces
	/// <c>Success_HostAlreadyInitialized</c> (1) / <c>Success_DifferentRuntimeProperties</c> (2)
	/// rather than the negative <c>0x80008081</c> a managed host gets. Exists because that
	/// distinction was reasoned about and never measured (persona sweep 2026-07-30, P1).
	/// </summary>
	private static int ProbeDoubleInit(string bridgeDirectory)
	{
		string runtimeConfigPath = Path.Combine(bridgeDirectory, "WinDbgAotExt.Bridge.runtimeconfig.json");
		if (!File.Exists(runtimeConfigPath)) { Console.Error.WriteLine("missing " + runtimeConfigPath); return 2; }

		// Reported BEFORE hostfxr is loaded, so the baseline is the process as the extension finds
		// it. This is what told us the shipped pre-flight must key on hostpolicy.dll: coreclr.dll is
		// never present at init time, so a coreclr-based guard would catch nothing.
		ReportHostingModules("baseline ");

		IntPtr hostfxrLibrary = NativeLibrary.Load(FindHostFxr());
		var initializeForRuntimeConfig = (delegate* unmanaged<char*, IntPtr, out IntPtr, int>)
			NativeLibrary.GetExport(hostfxrLibrary, "hostfxr_initialize_for_runtime_config");
		ReportHostingModules("post-load");

		// Each call is reported BEFORE the next one is attempted: if a call blocks, the transcript
		// still says WHICH one. (Learned immediately: the first run of this probe printed nothing
		// and hung, which is indistinguishable between the two calls without this.)
		int firstResult, secondResult;
		IntPtr firstContext, secondContext;
		fixed (char* runtimeConfigPathPointer = runtimeConfigPath)
		{
			Console.WriteLine("init #1: calling hostfxr_initialize_for_runtime_config ...");
			Console.Out.Flush();
			firstResult = initializeForRuntimeConfig(runtimeConfigPathPointer, IntPtr.Zero, out firstContext);
			Console.WriteLine($"init #1: hresult=0x{firstResult:X8} context=0x{firstContext.ToInt64():X}");
			ReportHostingModules("post-init");

			Console.WriteLine("init #2: calling hostfxr_initialize_for_runtime_config ...");
			Console.Out.Flush();
			secondResult = initializeForRuntimeConfig(runtimeConfigPathPointer, IntPtr.Zero, out secondContext);
			Console.WriteLine($"init #2: hresult=0x{secondResult:X8} context=0x{secondContext.ToInt64():X}");
			Console.Out.Flush();
		}

		Console.WriteLine($"second init passes the shipped `hresult < 0` guard: {secondResult >= 0}");
		Console.WriteLine(secondResult switch
		{
			0 => "  => plain Success (the guard's assumption holds)",
			1 => "  => Success_HostAlreadyInitialized: a runtime was ALREADY hosted; the config we asked for was NOT applied",
			2 => "  => Success_DifferentRuntimeProperties: already hosted with DIFFERENT properties than we requested",
			_ => "  => failure code (the shipped guard rejects it)",
		});
		return 0;
	}

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandleW(string moduleName);

	/// <summary>Which hosting modules are present at this point -- the candidate signals a pre-flight could key on.</summary>
	private static void ReportHostingModules(string stage)
	{
		string[] modules = { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clr.dll" };
		var present = new System.Collections.Generic.List<string>();
		foreach (string module in modules)
			if (GetModuleHandleW(module) != IntPtr.Zero) present.Add(module);
		Console.WriteLine($"{stage} loaded: {(present.Count == 0 ? "(none)" : string.Join(", ", present))}");
		Console.Out.Flush();
	}

	private static string FindHostFxr()
	{
		string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
		string frameworkResolverBase = Path.Combine(dotnetRoot, "host", "fxr");
		// Newest hostfxr wins -- ordered NUMERICALLY, not lexically. Ported from ClrHost 2026-07-30:
		// a plain string sort puts "9.0.4" AFTER "10.0.0" ('9' > '1'), so the old OrderBy(directory)
		// picked the OLDEST runtime on a machine with both 9.x and 10.x -- and the bridge needs .NET 10.
		// The fix landed in the shipped path and this twin kept the bug (persona sweep 2026-07-30, D2).
		string? hostfxrPath = Directory.Exists(frameworkResolverBase)
			? Directory.GetDirectories(frameworkResolverBase)
				.OrderBy(directory => Version.TryParse(Path.GetFileName(directory), out Version? version)
					? version
					: new Version(0, 0))
				.Select(directory => Path.Combine(directory, "hostfxr.dll"))
				.LastOrDefault(File.Exists)
			: null;
		return hostfxrPath ?? throw new FileNotFoundException("hostfxr.dll not found under " + frameworkResolverBase);
	}
}
