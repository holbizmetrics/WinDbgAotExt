using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt.Host;

// Boots CoreCLR in-process via hostfxr and calls the managed Bridge — the make-or-break Layer-2 seam.
internal static unsafe class Program
{
	const int hdt_load_assembly_and_get_function_pointer = 5;

	static int Main(string[] args)
	{
		if (args.Length < 1)
		{
			Console.Error.WriteLine("usage: Host <bridge-output-dir>");
			return 2;
		}
		string bridgeDir = args[0];
		string bridgeDll = Path.Combine(bridgeDir, "WinDbgAotExt.Bridge.dll");
		string bridgeCfg = Path.Combine(bridgeDir, "WinDbgAotExt.Bridge.runtimeconfig.json");
		if (!File.Exists(bridgeCfg)) { Console.Error.WriteLine("missing " + bridgeCfg); return 2; }

		string hostfxr = FindHostFxr();
		Console.WriteLine("hostfxr : " + hostfxr);
		IntPtr fxr = NativeLibrary.Load(hostfxr);

		var init = (delegate* unmanaged<char*, IntPtr, out IntPtr, int>)
			NativeLibrary.GetExport(fxr, "hostfxr_initialize_for_runtime_config");
		var getDel = (delegate* unmanaged<IntPtr, int, out IntPtr, int>)
			NativeLibrary.GetExport(fxr, "hostfxr_get_runtime_delegate");
		var close = (delegate* unmanaged<IntPtr, int>)
			NativeLibrary.GetExport(fxr, "hostfxr_close");

		int rc;
		IntPtr ctx;
		fixed (char* cfg = bridgeCfg)
			rc = init(cfg, IntPtr.Zero, out ctx);
		// 0=Success, 1=Success_HostAlreadyInitialized, 2=Success_DifferentRuntimeProperties
		if (rc < 0 || ctx == IntPtr.Zero) { Console.Error.WriteLine($"initialize failed 0x{rc:X8}"); return 3; }
		Console.WriteLine($"initialize: rc=0x{rc:X8} ctx=0x{ctx.ToInt64():X}");

		rc = getDel(ctx, hdt_load_assembly_and_get_function_pointer, out IntPtr loadFnPtr);
		if (rc != 0 || loadFnPtr == IntPtr.Zero) { Console.Error.WriteLine($"get_runtime_delegate failed 0x{rc:X8}"); close(ctx); return 4; }
		var load = (delegate* unmanaged<char*, char*, char*, char*, void*, void**, int>)loadFnPtr;

		IntPtr pingPtr;
		fixed (char* asm = bridgeDll)
		fixed (char* typ = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* mth = "Ping")
		{
			void* fp;
			rc = load(asm, typ, mth, null, null, &fp);
			pingPtr = (IntPtr)fp;
		}
		if (rc != 0 || pingPtr == IntPtr.Zero) { Console.Error.WriteLine($"load_assembly_and_get_function_pointer failed 0x{rc:X8}"); close(ctx); return 5; }

		var ping = (delegate* unmanaged<IntPtr, int, int>)pingPtr;
		int pingResult = ping(IntPtr.Zero, 0);
		Console.WriteLine($"Ping returned: {pingResult}  (expected 4242)");

		// --- Step 2: get Eval (an [UnmanagedCallersOnly] method) and run live C# through Roslyn ---
		// delegate_type_name = UNMANAGEDCALLERSONLY_METHOD = (char_t*)-1 → return the method's own ptr.
		IntPtr evalPtr;
		fixed (char* asm = bridgeDll)
		fixed (char* typ = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* mth = "Eval")
		{
			void* fp;
			rc = load(asm, typ, mth, (char*)(nint)(-1), null, &fp);
			evalPtr = (IntPtr)fp;
		}
		if (rc != 0 || evalPtr == IntPtr.Zero) { Console.Error.WriteLine($"get Eval failed 0x{rc:X8}"); close(ctx); return 6; }
		var eval = (delegate* unmanaged<IntPtr, IntPtr>)evalPtr;

		foreach (string code in args.Length > 1 ? args[1..] : new[] { "1 + 2", "Enumerable.Range(1,10).Where(x => x % 2 == 0).Sum()" })
		{
			IntPtr codePtr = Marshal.StringToHGlobalUni(code);
			IntPtr resPtr = eval(codePtr);
			string res = Marshal.PtrToStringUni(resPtr) ?? "(null)";
			Console.WriteLine($"  eval(\"{code}\") = {res}");
			Marshal.FreeHGlobal(codePtr);
			Marshal.FreeHGlobal(resPtr);
		}

		close(ctx);
		return pingResult == 4242 ? 0 : 1;
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
}
