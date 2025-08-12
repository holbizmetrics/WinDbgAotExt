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
}
