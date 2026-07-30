using System;
using System.Text;

namespace WinDbgAotExt;

/// <summary>
/// GUIDs and a minimal pattern to call COM vtables from C# Native AOT. Fleshed out only as far as
/// needed, to keep AOT size and risk low.
/// </summary>
public static unsafe class DbgEng
{
	/// <summary>IID of IDebugClient.</summary>
	public static readonly Guid IID_IDebugClient = new("27fe5639-8407-4f47-8364-ee118fb08ac8");

	/// <summary>IID of IDebugControl (&lt;= v4 baseline).</summary>
	public static readonly Guid IID_IDebugControl = new("5182e668-105e-416e-ad92-24ef800424ba");

	/// <summary>Output control: route to this client only.</summary>
	public const uint DEBUG_OUTCTL_THIS_CLIENT = 0x00000000;

	/// <summary>Output mask: normal output.</summary>
	public const uint DEBUG_OUTPUT_NORMAL = 0x00000001;

	/// <summary>QueryInterface helper for raw COM pointers (IUnknown*).</summary>
	/// <returns>The HRESULT from IUnknown::QueryInterface.</returns>
	public static int QueryInterface(IntPtr unknownPointer, in Guid iid, out IntPtr interfacePointer)
	{
		var vtable = *(nint**)unknownPointer;
		var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, in Guid, out IntPtr, int>)vtable[0];
		return queryInterface(unknownPointer, iid, out interfacePointer);
	}

	/// <summary>IUnknown::AddRef on a raw COM pointer.</summary>
	public static uint AddRef(IntPtr unknownPointer)
	{
		var vtable = *(nint**)unknownPointer;
		var addRef = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[1];
		return addRef(unknownPointer);
	}

	/// <summary>IUnknown::Release on a raw COM pointer.</summary>
	public static uint Release(IntPtr unknownPointer)
	{
		var vtable = *(nint**)unknownPointer;
		var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2];
		return release(unknownPointer);
	}

	// Output is a printf-style VARARGS method -- STDMETHODV(Output)(ULONG Mask, PCSTR Format, ...)
	// in dbgeng.h -- so whatever is passed here is parsed by the engine as a FORMAT STRING. Anything
	// reaching this function must already have its '%' escaped (see EscapeFormat); we pass zero
	// varargs, so an unescaped conversion makes the engine read arguments that were never pushed:
	// '%s' dereferences a garbage pointer and '%n' is a WRITE. Repro'd live before the fix --
	// "!echo 100%s and %x" printed "100" and swallowed the rest.
	// Beyond ~1 KB the copy goes on the heap: this runs on the engine's own callback thread, and a
	// stackalloc sized by (unbounded) command output -- e.g. a heap dump from !cs -- would overflow
	// its stack and fail-fast the debugger.
	private const int StackCopyLimit = 1024;

	/// <summary>
	/// Call IDebugControl::Output with an already-escaped UTF-8 buffer. Output is printf-VARARGS in
	/// dbgeng.h, so the text MUST have its <c>%</c> escaped before it reaches this call (see the
	/// comment on <see cref="StackCopyLimit"/> for the live-repro'd corruption class). Vtable index 14
	/// — NOT 8 (index 8 is OpenLogFile), verified against dbgeng.h; that was the bug.
	/// </summary>
	/// <param name="pControl">IDebugControl pointer.</param>
	/// <param name="mask">DEBUG_OUTPUT_* mask.</param>
	/// <param name="utf8NoNul">UTF-8 text without a trailing NUL (this method terminates it).</param>
	public static int ControlOutput(IntPtr pControl, uint mask, ReadOnlySpan<byte> utf8NoNul)
	{
		var vtable = *(nint**)pControl;
		var output = (delegate* unmanaged[Stdcall]<IntPtr, uint, sbyte*, int>)vtable[14];

		if (utf8NoNul.Length + 1 > StackCopyLimit)
		{
			byte[] heapBuffer = new byte[utf8NoNul.Length + 1]; // zero-filled => already NUL-terminated
			utf8NoNul.CopyTo(heapBuffer);
			fixed (byte* heapPointer = heapBuffer)
				return output(pControl, mask, (sbyte*)heapPointer);
		}

		fixed (byte* sourcePointer = utf8NoNul)
		{
			// Ensure NUL-terminated buffer
			var terminatedBuffer = stackalloc byte[utf8NoNul.Length + 1];
			for (int i = 0; i < utf8NoNul.Length; i++) terminatedBuffer[i] = sourcePointer[i];
			terminatedBuffer[utf8NoNul.Length] = 0;

			return output(pControl, mask, (sbyte*)terminatedBuffer);
		}
	}

	/// <summary>
	/// Neutralize printf conversions in text that is DATA, not a format: the engine collapses
	/// <c>%%</c> back to a single <c>%</c>, so the operator still reads what the command meant to print.
	/// </summary>
	internal static string EscapeFormat(string text) => text.Replace("%", "%%");

	/// <summary>
	/// Print one line to the debugger console, escaping printf conversions and appending the newline.
	/// The safe default every command output path should use.
	/// </summary>
	public static void DbgOutLine(IntPtr pControl, string text)
	{
		if (pControl == IntPtr.Zero) return;
		if (!text.EndsWith("\n")) text += "\n";
		var bytes = Encoding.UTF8.GetBytes(EscapeFormat(text));
		_ = ControlOutput(pControl, DEBUG_OUTPUT_NORMAL, bytes);
	}
}
