#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinDbgAotExt;

/// <summary>
/// LAYER 2 inside the extension: boots CoreCLR in-process (via hostfxr) and calls the managed Bridge,
/// so a WinDbg command can run live C# through Roslyn. The extension is NativeAOT (no CoreCLR of its
/// own), so our hostfxr_initialize_for_runtime_config is the FIRST init in the debugger's process —
/// which is exactly why this works (proven in the host/ spike; this ports it into the .load'able DLL).
/// </summary>
internal static unsafe class ClrHost
{
	private const int RuntimeDelegateLoadAssemblyAndGetFunctionPointer = 5;
	private const int GetModuleHandleExFlagFromAddress = 0x4;
	private const int GetModuleHandleExFlagUnchangedRefcount = 0x2;

	/// <summary>
	/// Escape hatch for the pre-flight refusal below: set to 1/true to attempt the boot anyway on a
	/// process that already has a CLR. A permanently-closed door with no override becomes the thing
	/// people work around by patching the DLL.
	/// </summary>
	internal const string ForceBootVariable = "WINDBGAOTEXT_FORCE_BOOT";

	private static bool _isBooted;
	private static string? _bridgeDllPath;
	private static delegate* unmanaged<char*, char*, char*, char*, void*, void**, int> _loadAssemblyAndGetFunctionPointer;

	// A method whose address lies inside THIS DLL — used to locate our own module path.
	[UnmanagedCallersOnly] private static void ModuleAnchor() { }

	/// <summary>Boots the runtime once (cached).</summary>
	/// <returns>Null on success, else a human-readable error string.</returns>
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
				// Say what to DO, not just what is missing: the natural mistake is to `.load` the raw
				// publish output (which is what `dotnet publish` prints), where no bridge/ exists.
				return "bridge runtimeconfig not found next to extension: " + runtimeConfigPath
					+ Environment.NewLine
					+ "  This DLL needs a 'bridge' folder ALONGSIDE it. Load the deploy bundle instead"
					+ " (deploy\\WinDbgAotExt.dll, or the CI 'WinDbgAotExt-bundle' artifact / release zip),"
					+ " or copy the bridge build output next to this DLL. See README 'What you load'.";

			// PRE-FLIGHT: the whole Layer-2 design rests on "our init is the FIRST init in this
			// process". MEASURED 2026-07-30 (`Host --probe-double-init`, AOT, hostfxr 10.x): when a
			// runtime is already initialized and its context still open, the second
			// hostfxr_initialize_for_runtime_config DOES NOT RETURN -- it blocks indefinitely. So the
			// hresult guard below never gets to judge: the debugger simply hangs, which reads to the
			// operator as "the extension wedged WinDbg". Refuse up front instead, with a message that
			// names the cause. (A managed host is a different, already-handled case: it fails 0x80008081.)
			string? clrConflict = DetectExistingClr();
			if (clrConflict != null) return clrConflict;

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
			string? initProblem = ClassifyInitResult(hresult, hostContext != IntPtr.Zero);
			if (initProblem != null) return initProblem;

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

	/// <summary>Step-3a de-risk: prove the extension can reach managed CoreCLR code.</summary>
	/// <returns>4242 on success; -1 on boot or load failure.</returns>
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

	/// <summary>
	/// Compile + run live C# via Roslyn in the hosted CoreCLR, handing the script the debugger client
	/// so it can reach the live target (Debugger.Exec, ...).
	/// </summary>
	public static string Eval(string sourceCode, IntPtr debugClient) =>
		CallBridge("Eval", sourceCode, debugClient);

	/// <summary>
	/// Drop the persistent !cs session state (every variable the operator declared). The bridge owns
	/// the state; this is just the native-side route to it.
	/// </summary>
	public static string ResetScriptState() =>
		CallBridge("ResetScriptState", string.Empty, IntPtr.Zero);

	/// <summary>
	/// Inspect one managed object's fields (<c>!fields</c>). The address (as text) + debug client go
	/// to the bridge, which does the ClrMD read and returns a formatted listing.
	/// </summary>
	public static string Fields(string addressText, IntPtr debugClient) =>
		CallBridge("FieldsText", addressText, debugClient);

	/// <summary>List the persistent !cs session's variables (<c>!csvars</c>). State lives in the bridge.</summary>
	public static string SessionVars() =>
		CallBridge("SessionVars", string.Empty, IntPtr.Zero);

	/// <summary>Filter the managed heap for strings (<c>!strings [pattern]</c>). Args text + client go to the bridge.</summary>
	public static string Strings(string arguments, IntPtr debugClient) =>
		CallBridge("StringsText", arguments, debugClient);

	/// <summary>Write the triage report (<c>!report [path]</c>). Path text + client go to the bridge.</summary>
	public static string WriteReport(string path, IntPtr debugClient) =>
		CallBridge("WriteReport", path, debugClient);

	// One route to any (string, IntPtr) -> string entry point on the bridge. Both bridge methods are
	// UNMANAGEDCALLERSONLY and return an HGlobal UTF-16 string that WE own and must free.
	private static string CallBridge(string bridgeMethodName, string argumentText, IntPtr debugClient)
	{
		var error = EnsureBooted();
		if (error != null) return "CLR boot failed: " + error;
		IntPtr bridgeFunctionPointer;
		fixed (char* assemblyPath = _bridgeDllPath)
		fixed (char* typeName = "WinDbgAotExt.Bridge.Bridge, WinDbgAotExt.Bridge")
		fixed (char* methodName = bridgeMethodName)
		{
			void* functionPointer;
			int hresult = _loadAssemblyAndGetFunctionPointer(assemblyPath, typeName, methodName,
				(char*)(nint)(-1), null, &functionPointer); // (char*)-1 = UNMANAGEDCALLERSONLY_METHOD
			if (hresult != 0) return $"load {bridgeMethodName} failed 0x{hresult:X8}";
			bridgeFunctionPointer = (IntPtr)functionPointer;
		}
		var callBridgeEntryPoint = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)bridgeFunctionPointer; // (textUtf16, debugClient) -> resultUtf16
		IntPtr argumentPointer = Marshal.StringToHGlobalUni(argumentText);
		IntPtr resultPointer = callBridgeEntryPoint(argumentPointer, debugClient);
		string result = Marshal.PtrToStringUni(resultPointer) ?? "(null)";
		Marshal.FreeHGlobal(argumentPointer);
		if (resultPointer != IntPtr.Zero) Marshal.FreeHGlobal(resultPointer);
		return result;
	}

	/// <summary>
	/// hostfxr's documented outcomes for <c>hostfxr_initialize_for_runtime_config</c>, classified for
	/// a human. Pure (no native calls) so every branch is unit-tested.
	/// <para><c>0</c> Success · <c>1</c> Success_HostAlreadyInitialized · <c>2</c>
	/// Success_DifferentRuntimeProperties — 1 and 2 mean a runtime was ALREADY hosted and the config
	/// we asked for was not (fully) applied, so the bridge's .NET 10 request may be silently unmet;
	/// they pass a bare <c>hresult &lt; 0</c> test, which is why this exists. <c>0x80008081</c> is the
	/// managed-host case the README already documents.</para>
	/// </summary>
	/// <param name="hresult">The value hostfxr returned.</param>
	/// <param name="hasContext">Whether a non-null host context came back.</param>
	/// <returns>Null when the boot may proceed; otherwise the operator-facing refusal.</returns>
	internal static string? ClassifyInitResult(int hresult, bool hasContext)
	{
		if (hresult < 0)
		{
			string hint = (uint)hresult == 0x80008081u
				? " -- a CLR is already loaded in this process (a managed host cannot host a second"
					+ " runtime). Load the extension into cdb/WinDbg, which is a native host."
				: "";
			return $"hostfxr init failed 0x{hresult:X8}{hint}";
		}
		if (!hasContext) return $"hostfxr init returned 0x{hresult:X8} but no host context";
		if (hresult == 1 || hresult == 2)
			return "hostfxr reports a runtime was ALREADY initialized in this process"
				+ $" (0x{hresult:X8}), so the bridge's requested runtime (.NET 10) may not be the one"
				+ " loaded. Refusing rather than running against an unknown runtime. Use cdb/WinDbg"
				+ $" (a native host), or set {ForceBootVariable}=1 to try anyway.";
		return null;
	}

	/// <summary>
	/// Pre-flight guard for the "first init wins" precondition. Returns null when the process looks
	/// clean, otherwise the operator-facing refusal. Overridable via
	/// <see cref="ForceBootVariable"/> — measured behavior when the precondition is violated is a
	/// HANG, so refusing early is strictly better than letting the debugger freeze.
	/// </summary>
	private static string? DetectExistingClr()
	{
		if (IsForceBootRequested(Environment.GetEnvironmentVariable(ForceBootVariable))) return null;
		// The module list is MEASURED, not guessed (`Host --probe-double-init`, AOT, hostfxr 10.x):
		//   after loading hostfxr, before init : hostfxr.dll
		//   after a successful init            : hostfxr.dll + hostpolicy.dll
		//   coreclr.dll                        : NEVER at this stage
		// initialize_for_runtime_config creates a host CONTEXT; the runtime itself is not loaded
		// until a delegate is requested. So the signal for "a host context already exists here" --
		// the state that made the second init BLOCK FOREVER -- is hostpolicy.dll, not coreclr.dll.
		// (A first draft of this guard keyed on coreclr.dll and would have caught nothing; the probe
		// is what said so.) coreclr/clr stay in the list for the already-loaded-runtime case, which
		// hostfxr rejects with 0x80008081 rather than hanging.
		// hostfxr.dll is deliberately NOT a trigger: this method runs before we load it ourselves,
		// but its mere presence does not prove a context exists, and refusing where hosting would
		// have worked is the one way this guard could be worse than the bug it prevents.
		foreach (string hostingModule in new[] { "hostpolicy.dll", "coreclr.dll", "clr.dll" })
		{
			if (GetModuleHandleW(hostingModule) != IntPtr.Zero)
				return $"refusing to boot: '{hostingModule}' is ALREADY loaded in this process, so"
					+ " this would not be the first runtime init. Measured behavior in that case is a"
					+ " HANG (the second hostfxr initialize never returns), which looks like the"
					+ " extension wedging the debugger. This extension is proven in cdb / native"
					+ " WinDbg; a .NET-hosting debugger front-end is not supported."
					+ $" Set {ForceBootVariable}=1 to attempt it anyway.";
		}
		return null;
	}

	/// <summary>True for the values a human means by "on" — pure, so the parsing is unit-tested.</summary>
	internal static bool IsForceBootRequested(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return false;
		string trimmed = value.Trim();
		return trimmed.Equals("1", StringComparison.Ordinal)
			|| trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
			|| trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
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

	// Newest hostfxr wins -- ordered NUMERICALLY, not lexically. A plain string sort puts "9.0.4"
	// AFTER "10.0.0" ('9' > '1'), so the old OrderBy(directory) picked the OLDEST runtime the moment
	// a machine had both 9.x and 10.x installed -- and the bridge needs .NET 10. Unparseable names
	// sort to the bottom rather than throwing.
	private static string FindHostFxr()
	{
		string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? @"C:\Program Files\dotnet";
		string frameworkResolverBase = Path.Combine(dotnetRoot, "host", "fxr");
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

	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandleW(string moduleName);
	[DllImport("kernel32", SetLastError = true)]
	private static extern bool GetModuleHandleExW(int flags, IntPtr address, out IntPtr module);
	[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetModuleFileNameW(IntPtr module, char* filename, uint size);
}
