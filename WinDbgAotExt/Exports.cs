using System;
using System.Runtime.InteropServices;

namespace WinDbgAotExt;

/// <summary>
/// The native surface WinDbg sees: every <c>[UnmanagedCallersOnly]</c> export dbgeng can call.
/// The three DebugExtension* exports form the extension lifecycle; every command export is a
/// one-line route into <see cref="CommandHost.Run"/> by command name.
/// </summary>
public static unsafe class Exports
{
	private const int S_OK = 0;
	private const int E_FAIL = unchecked((int)0x80004005);

	private const uint EXT_VERSION = (CommandHost.EXT_VERSION_MAJOR << 16) | CommandHost.EXT_VERSION_MINOR;

	/// <summary>Extension entry point: report our version/flags to the engine. Never throws across the ABI.</summary>
	[UnmanagedCallersOnly(EntryPoint = "DebugExtensionInitialize")]
	public static int DebugExtensionInitialize(uint* version, uint* flags)
	{
		try
		{
			if (version != null) *version = EXT_VERSION;
			if (flags != null) *flags = 0;
			return S_OK;
		}
		catch { return E_FAIL; }
	}

	/// <summary>Extension teardown; nothing to release (the hosted CoreCLR cannot be unloaded anyway).</summary>
	[UnmanagedCallersOnly(EntryPoint = "DebugExtensionUninitialize")]
	public static void DebugExtensionUninitialize() { }

	/// <summary>Engine notifications (session active/inactive etc.); deliberately ignored.</summary>
	[UnmanagedCallersOnly(EntryPoint = "DebugExtensionNotify")]
	public static void DebugExtensionNotify(uint notify, ulong argument) { }

	/// <summary><c>!hello</c> — proof-of-life command.</summary>
	[UnmanagedCallersOnly(EntryPoint = "hello")]
	public static int Hello(IntPtr client, byte* args) => CommandHost.Run("hello", client, args);

	/// <summary><c>!echo</c> — echo the arguments back (also the printf-escaping test surface).</summary>
	[UnmanagedCallersOnly(EntryPoint = "echo")]
	public static int Echo(IntPtr client, byte* args) => CommandHost.Run("echo", client, args);

	/// <summary><c>!version</c> — print the extension version.</summary>
	[UnmanagedCallersOnly(EntryPoint = "version")]
	public static int Version(IntPtr client, byte* args) => CommandHost.Run("version", client, args);

	/// <summary><c>!clrtest</c> — boot the hosted CoreCLR and prove the bridge answers (Ping).</summary>
	[UnmanagedCallersOnly(EntryPoint = "clrtest")]
	public static int ClrTest(IntPtr client, byte* args) => CommandHost.Run("clrtest", client, args);

	/// <summary><c>!cs</c> — run live C# via Roslyn in the hosted CoreCLR (persistent session).</summary>
	[UnmanagedCallersOnly(EntryPoint = "cs")]
	public static int Cs(IntPtr client, byte* args) => CommandHost.Run("cs", client, args);

	/// <summary><c>!csreset</c> — clear the persistent !cs session state (variables declared at the !cs prompt).</summary>
	[UnmanagedCallersOnly(EntryPoint = "csreset")]
	public static int CsReset(IntPtr client, byte* args) => CommandHost.Run("csreset", client, args);

	/// <summary><c>!csvars</c> — list the persistent !cs session's variables.</summary>
	[UnmanagedCallersOnly(EntryPoint = "csvars")]
	public static int CsVars(IntPtr client, byte* args) => CommandHost.Run("csvars", client, args);

	/// <summary><c>!fields</c> — inspect one managed object's instance fields by address.</summary>
	[UnmanagedCallersOnly(EntryPoint = "fields")]
	public static int Fields(IntPtr client, byte* args) => CommandHost.Run("fields", client, args);

	/// <summary><c>!strings</c> — filter the managed heap for strings (optional regex).</summary>
	[UnmanagedCallersOnly(EntryPoint = "strings")]
	public static int Strings(IntPtr client, byte* args) => CommandHost.Run("strings", client, args);

	/// <summary><c>!report</c> — write the standard triage battery to one markdown file.</summary>
	[UnmanagedCallersOnly(EntryPoint = "report")]
	public static int Report(IntPtr client, byte* args) => CommandHost.Run("report", client, args);

	/// <summary><c>!wiltriage</c> — triage the current break (benign deliberate break vs real fault + culprit module).</summary>
	[UnmanagedCallersOnly(EntryPoint = "wiltriage")]
	public static int Wiltriage(IntPtr client, byte* args) => CommandHost.Run("wiltriage", client, args);
}
