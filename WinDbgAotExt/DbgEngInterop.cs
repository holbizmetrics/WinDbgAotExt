using System;
using System.Text;

namespace WinDbgAotExt;

// This file gives you GUIDs and a minimal pattern to call COM vtables from C# Native AOT.
// Flesh out only what you need to keep AOT size and risk low.

public static unsafe class DbgEng
{
	// Common interface IIDs (add more as needed)
	public static readonly Guid IID_IDebugClient = new("27fe5639-8407-4f47-8364-ee118fb08ac8");
	public static readonly Guid IID_IDebugControl = new("5182e668-105e-416e-ad92-24ef800424ba"); // IDebugControl(<= v4) baseline

	// DEBUG_OUTPUT flags etc. (add selectively)
	public const uint DEBUG_OUTCTL_THIS_CLIENT = 0x00000000;
	public const uint DEBUG_OUTPUT_NORMAL = 0x00000001;

	// QueryInterface helper for raw COM pointers (IUnknown*)
	public static int QueryInterface(IntPtr unknownPointer, in Guid iid, out IntPtr interfacePointer) // returns HRESULT
	{
		var vtable = *(nint**)unknownPointer;
		var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)vtable[0];
		return queryInterface(unknownPointer, iid, out interfacePointer);
	}

	public static uint AddRef(IntPtr unknownPointer)
	{
		var vtable = *(nint**)unknownPointer;
		var addRef = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[1];
		return addRef(unknownPointer);
	}

	public static uint Release(IntPtr unknownPointer)
	{
		var vtable = *(nint**)unknownPointer;
		var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
		return release(unknownPointer);
	}

	// Example: call IDebugControl::Output (simplified for baseline interface)
	// HRESULT Output(ULONG Mask, PCSTR Format, ...);
	// The real signature is varargs; DbgEng also exposes OutputVa. To keep it simple,
	// you can define a shim for a fixed string without format args if you target a newer interface,
	// or use OutputWide if convenient. Here is a minimal "fixed format" call using UTF-8.

	public static int ControlOutput(IntPtr pControl, uint mask, ReadOnlySpan<byte> utf8NoNul)
	{
		// IDebugControl vtable: after IUnknown (0/1/2), Output is index 14 — NOT 8
		// (index 8 is OpenLogFile). Verified against dbgeng.h. This was the bug.
		var vtable = *(nint**)pControl;
		var output = (delegate* unmanaged[Stdcall]<IntPtr, uint, sbyte*, int>)vtable[14];

		fixed (byte* sourcePointer = utf8NoNul)
		{
			// Ensure NUL-terminated buffer
			var terminatedBuffer = stackalloc byte[utf8NoNul.Length + 1];
			for (int i = 0; i < utf8NoNul.Length; i++) terminatedBuffer[i] = sourcePointer[i];
			terminatedBuffer[utf8NoNul.Length] = 0;

			return output(pControl, mask, (sbyte*)terminatedBuffer);
		}
	}

	public static void DbgOutLine(IntPtr pControl, string text)
	{
		if (pControl == IntPtr.Zero) return;
		if (!text.EndsWith("\n")) text += "\n";
		var bytes = Encoding.UTF8.GetBytes(text);
		_ = ControlOutput(pControl, DEBUG_OUTPUT_NORMAL, bytes);
	}
}
