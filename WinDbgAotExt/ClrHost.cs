#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt;

// LAYER 2 inside the extension: boots CoreCLR in-process (via hostfxr) and calls the managed Bridge,
// so a WinDbg command can run live C# through Roslyn. The extension is NativeAOT (no CoreCLR of its
// own), so our hostfxr_initialize_for_runtime_config is the FIRST init in the debugger's process —
// which is exactly why this works (proven in the layer2/ spike; this ports it into the .load'able DLL).
internal static unsafe class ClrHost
{
	const int hdt_load_assembly_and_get_function_pointer = 5;
	const int GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
	const int GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

	static bool _booted;
	static string? _bridgeDll;
	static delegate* unmanaged<char*, char*, char*, char*, void*, void**, int> _load;

	// Anchor whose address lies inside THIS DLL — used to locate our own module path.
	[UnmanagedCallersOnly] static void Anchor() { }

	// Boots the runtime once (cached). Returns null on success, else an error string.
	public static string? EnsureBooted()
	{
		if (_booted) return null;
		try
		{
			string extDir = GetOwnDirectory();
			string bridgeDir = Path.Combine(extDir, "bridge");
			_bridgeDll = Path.Combine(bridgeDir, "WinDbgAotExt.Bridge.dll");
			string cfg = Path.Combine(bridgeDir, "WinDbgAotExt.Bridge.runtimeconfig.json");
			if (!File.Exists(cfg)) return "bridge runtimeconfig not found next to extension: " + cfg;

			string hostfxr = FindHostFxr();
			IntPtr fxr = NativeLibrary.Load(hostfxr);
			var init = (delegate* unmanaged<char*, IntPtr, out IntPtr, int>)
				NativeLibrary.GetExport(fxr, "hostfxr_initialize_for_runtime_config");
			var getDel = (delegate* unmanaged<IntPtr, int, out IntPtr, int>)
				NativeLibrary.GetExport(fxr, "hostfxr_get_runtime_delegate");

			int rc; IntPtr ctx;
			fixed (char* c = cfg) rc = init(c, IntPtr.Zero, out ctx);
			if (rc < 0 || ctx == IntPtr.Zero) return $"hostfxr init failed 0x{rc:X8}";
			rc = getDel(ctx, hdt_load_assembly_and_get_function_pointer, out IntPtr loadPtr);
			if (rc != 0 || loadPtr == IntPtr.Zero) return $"get_runtime_delegate failed 0x{rc:X8}";

			_load = (delegate* unmanaged<char*, char*, char*, char*, void*, void**, int>)loadPtr;
			_booted = true;
			return null;
		}
		catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
	}

	// Step-3a de-risk: prove the extension can reach managed CoreCLR code. Returns 4242 on success.
	public static int Ping()
	{
		if (EnsureBooted() != null) return -1;
		IntPtr fp;
		fixed (char* asm = _bridgeDll)
		fixed (char* typ = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* mth = "Ping")
		{
			void* p; int rc = _load(asm, typ, mth, null, null, &p);
			if (rc != 0) return -1;
			fp = (IntPtr)p;
		}
		var ping = (delegate* unmanaged<IntPtr, int, int>)fp;
		return ping(IntPtr.Zero, 0);
	}

	// Step-3b: compile + run live C# via Roslyn in the hosted CoreCLR; returns the result string.
	public static string Eval(string code)
	{
		var err = EnsureBooted();
		if (err != null) return "CLR boot failed: " + err;
		IntPtr fp;
		fixed (char* asm = _bridgeDll)
		fixed (char* typ = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* mth = "Eval")
		{
			void* p; int rc = _load(asm, typ, mth, (char*)(nint)(-1), null, &p); // UNMANAGEDCALLERSONLY_METHOD
			if (rc != 0) return $"load Eval failed 0x{rc:X8}";
			fp = (IntPtr)p;
		}
		var eval = (delegate* unmanaged<IntPtr, IntPtr>)fp;
		IntPtr codePtr = Marshal.StringToHGlobalUni(code);
		IntPtr resPtr = eval(codePtr);
		string res = Marshal.PtrToStringUni(resPtr) ?? "(null)";
		Marshal.FreeHGlobal(codePtr);
		if (resPtr != IntPtr.Zero) Marshal.FreeHGlobal(resPtr);
		return res;
	}

	static string GetOwnDirectory()
	{
		delegate* unmanaged<void> anchor = &Anchor;
		if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
				(IntPtr)anchor, out IntPtr h))
			throw new InvalidOperationException("GetModuleHandleExW failed");
		char* buf = stackalloc char[520];
		uint n = GetModuleFileNameW(h, buf, 520);
		string own = new string(buf, 0, (int)n);
		return Path.GetDirectoryName(own) ?? throw new InvalidOperationException("no dir for " + own);
	}

	static string FindHostFxr()
	{
		string root = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
		string fxrBase = Path.Combine(root, "host", "fxr");
		string? hit = Directory.Exists(fxrBase)
			? Directory.GetDirectories(fxrBase).OrderBy(d => d)
				.Select(d => Path.Combine(d, "hostfxr.dll")).LastOrDefault(File.Exists)
			: null;
		return hit ?? throw new FileNotFoundException("hostfxr.dll not found under " + fxrBase);
	}

	[DllImport("kernel32", SetLastError = true)]
	static extern bool GetModuleHandleExW(int flags, IntPtr addr, out IntPtr module);
	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetModuleFileNameW(IntPtr module, char* filename, uint size);
}
