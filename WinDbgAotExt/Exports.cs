using System;
using System.Runtime.InteropServices;

namespace WinDbgAotExt;

public static unsafe class Exports
{
	private const int S_OK = 0;
	private const int E_FAIL = unchecked((int)0x80004005);

	private const uint EXT_VERSION = (CommandHost.EXT_VERSION_MAJOR << 16) | CommandHost.EXT_VERSION_MINOR;

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

	[UnmanagedCallersOnly(EntryPoint = "DebugExtensionUninitialize")]
	public static void DebugExtensionUninitialize() { }

	[UnmanagedCallersOnly(EntryPoint = "DebugExtensionNotify")]
	public static void DebugExtensionNotify(uint notify, ulong argument) { }

	// Exports → route to the host by name
	[UnmanagedCallersOnly(EntryPoint = "hello")]
	public static int Hello(IntPtr client, byte* args) => CommandHost.Run("hello", client, args);

	[UnmanagedCallersOnly(EntryPoint = "echo")]
	public static int Echo(IntPtr client, byte* args) => CommandHost.Run("echo", client, args);

	[UnmanagedCallersOnly(EntryPoint = "version")]
	public static int Version(IntPtr client, byte* args) => CommandHost.Run("version", client, args);

	// Layer 2 commands (boot CoreCLR + run live C# via Roslyn)
	[UnmanagedCallersOnly(EntryPoint = "clrtest")]
	public static int ClrTest(IntPtr client, byte* args) => CommandHost.Run("clrtest", client, args);

	[UnmanagedCallersOnly(EntryPoint = "cs")]
	public static int Cs(IntPtr client, byte* args) => CommandHost.Run("cs", client, args);

	// Clear the persistent !cs session state (variables declared at the !cs prompt)
	[UnmanagedCallersOnly(EntryPoint = "csreset")]
	public static int CsReset(IntPtr client, byte* args) => CommandHost.Run("csreset", client, args);

	// List the persistent !cs session's variables
	[UnmanagedCallersOnly(EntryPoint = "csvars")]
	public static int CsVars(IntPtr client, byte* args) => CommandHost.Run("csvars", client, args);

	// Inspect one managed object's instance fields by address
	[UnmanagedCallersOnly(EntryPoint = "fields")]
	public static int Fields(IntPtr client, byte* args) => CommandHost.Run("fields", client, args);

	// Filter the managed heap for strings (optional regex)
	[UnmanagedCallersOnly(EntryPoint = "strings")]
	public static int Strings(IntPtr client, byte* args) => CommandHost.Run("strings", client, args);

	// Write the standard triage battery to one markdown file
	[UnmanagedCallersOnly(EntryPoint = "report")]
	public static int Report(IntPtr client, byte* args) => CommandHost.Run("report", client, args);

	// Triage the current break (benign deliberate break vs real fault + culprit module)
	[UnmanagedCallersOnly(EntryPoint = "wiltriage")]
	public static int Wiltriage(IntPtr client, byte* args) => CommandHost.Run("wiltriage", client, args);
}
