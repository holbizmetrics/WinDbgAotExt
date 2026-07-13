using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using WinDbgAotExt;
using Xunit;

namespace WinDbgAotExt.Tests;

// Proves the native OUTPUT path that ArgvTests cannot reach - the layer the project memory
// called "NOT unit-testable (needs a real IDebugControl)". It IS testable: we hand-build fake
// COM objects with real native vtables. A !command, given a (mock) IDebugClient, must
// QueryInterface -> IDebugControl and call Output at VTABLE INDEX 14 (the 8->14 fix in
// DbgEngInterop). We slot a capturing function at index 14 and assert the exact bytes the
// command emitted. No WinDbg required; this is the crash-proof enter -> dispatch -> Output ->
// return round-trip, verified in-process.
public unsafe class DbgEngOutputTests
{
    // what the mock IDebugControl::Output received
    private static string? s_captured;
    // the mock IDebugControl the client's QueryInterface hands back
    private static IntPtr s_control;

    // --- mock vtable entries (native-callable; write only to statics) ---

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ClientQueryInterface(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        // hand back the mock control ONLY for IID_IDebugControl (what CommandHost.Run asks for)
        if (iid != null && *iid == DbgEng.IID_IDebugControl) { *ppv = s_control; return 0; }
        if (ppv != null) *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRefRelease(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Output(IntPtr self, uint mask, sbyte* text)
    {
        s_captured = Marshal.PtrToStringUTF8((IntPtr)text);
        return 0;
    }

    // --- build the two fake COM objects (object = pointer to a vtable of function pointers) ---

    private static IntPtr MakeObject(nint* vtable)
    {
        var comObject = (nint*)Marshal.AllocHGlobal(IntPtr.Size);
        comObject[0] = (nint)vtable;
        return (IntPtr)comObject;
    }

    private static IntPtr BuildMockClient()
    {
        // IDebugControl vtable: needs at least index 14. 0/1/2 = QI/AddRef/Release, 14 = Output.
        var controlVtable = (nint*)Marshal.AllocHGlobal(IntPtr.Size * 15);
        for (int i = 0; i < 15; i++) controlVtable[i] = 0;
        controlVtable[1]  = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        controlVtable[2]  = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        controlVtable[14] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint, sbyte*, int>)&Output;
        s_control = MakeObject(controlVtable);

        // IDebugClient vtable: 0 = QI (returns the control), 1/2 = AddRef/Release.
        var clientVtable = (nint*)Marshal.AllocHGlobal(IntPtr.Size * 3);
        clientVtable[0] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&ClientQueryInterface;
        clientVtable[1] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        clientVtable[2] = (nint)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        return MakeObject(clientVtable);
    }

    private static int RunCommand(string name, IntPtr client, string args)
    {
        var argumentBytes = Encoding.UTF8.GetBytes(args + "\0"); // CommandHost reads a NUL-terminated arg string
        fixed (byte* argumentPointer = argumentBytes) { return CommandHost.Run(name, client, argumentPointer); }
    }

    // --- tests ---

    [Fact]
    public void Hello_WithArgs_EmitsThroughOutputVtable14()
    {
        s_captured = null;
        int hresult = RunCommand("hello", BuildMockClient(), "world");
        Assert.Equal(0, hresult);
        Assert.Equal("Hello from C# Native AOT! args=[world]\n", s_captured);
    }

    [Fact]
    public void Hello_NoArgs_EmitsNoArgsForm()
    {
        s_captured = null;
        int hresult = RunCommand("hello", BuildMockClient(), "");
        Assert.Equal(0, hresult);
        Assert.Equal("Hello from C# Native AOT! (no args)\n", s_captured);
    }

    [Fact]
    public void Echo_EmitsRawThroughOutput()
    {
        s_captured = null;
        int hresult = RunCommand("echo", BuildMockClient(), "some raw text");
        Assert.Equal(0, hresult);
        Assert.Equal("some raw text\n", s_captured);
    }

    [Fact]
    public void Version_EmitsVersionString()
    {
        s_captured = null;
        int hresult = RunCommand("version", BuildMockClient(), "");
        Assert.Equal(0, hresult);
        Assert.Equal($"{CommandHost.EXT_VERSION_MAJOR}.{CommandHost.EXT_VERSION_MINOR}\n", s_captured);
    }

    // --- the printf-format-string class (audit HIGH, repro'd live before the fix) ---
    // IDebugControl::Output is VARARGS: STDMETHODV(Output)(ULONG Mask, PCSTR Format, ...). Text handed
    // to it is parsed as a FORMAT, and we pass zero varargs -- so an unescaped '%s' made the engine
    // dereference a garbage pointer and '%n' WRITE through one. Live repro before the fix:
    // "!echo 100%s and %x" printed "100" and swallowed the rest. The mock cannot printf-parse, so these
    // tests assert the ESCAPING contract at the boundary instead: every '%' reaching Output is doubled,
    // which the engine collapses back to a single '%' for the operator.

    [Fact]
    public void Echo_PercentConversions_AreEscapedBeforeReachingOutput()
    {
        s_captured = null;
        int hresult = RunCommand("echo", BuildMockClient(), "100%s and %x and %n");
        Assert.Equal(0, hresult);
        // What the engine receives: conversions neutralized (%% renders as a literal % downstream).
        Assert.Equal("100%%s and %%x and %%n\n", s_captured);
        // Every '%' must be part of a "%%" pair -- i.e. no lone '%' can start a conversion. (Naive
        // "does not contain %s" would be wrong: "100%%s" contains "%s" as a substring by construction.)
        Assert.All(
            Enumerable.Range(0, s_captured!.Length).Where(index => s_captured[index] == '%'),
            index => Assert.True(
                (index > 0 && s_captured[index - 1] == '%') ||
                (index + 1 < s_captured.Length && s_captured[index + 1] == '%'),
                $"unescaped '%' at position {index} would be read as a printf conversion"));
    }

    [Fact]
    public void Output_PlainText_IsUnchangedByEscaping()
    {
        // The escape must not disturb ordinary output -- the regression that would make the fix worse
        // than the bug.
        s_captured = null;
        int hresult = RunCommand("echo", BuildMockClient(), "no conversions here");
        Assert.Equal(0, hresult);
        Assert.Equal("no conversions here\n", s_captured);
    }

    [Fact]
    public void Output_LargePayload_GoesThroughHeapPathIntact()
    {
        // Beyond the stack-copy limit the buffer is heap-allocated: !cs output (a heap dump, a full
        // `k`) is unbounded and a stackalloc that size would overflow the ENGINE's callback thread and
        // fail-fast the debugger. Assert the big payload still arrives byte-exact and NUL-terminated.
        s_captured = null;
        string largeText = new string('A', 64 * 1024);
        int hresult = RunCommand("echo", BuildMockClient(), largeText);
        Assert.Equal(0, hresult);
        Assert.Equal(largeText + "\n", s_captured);
    }

    [Fact]
    public void NullClient_NoOutput_ButStillSucceeds()
    {
        // WinDbg can pass a NULL client; CommandHost skips QueryInterface and DbgOutLine no-ops.
        s_captured = null;
        int hresult = RunCommand("hello", IntPtr.Zero, "x");
        Assert.Equal(0, hresult);
        Assert.Null(s_captured);
    }

    [Fact]
    public void UnknownCommand_ReturnsFail_NoCrash()
    {
        s_captured = null;
        int hresult = RunCommand("does_not_exist", BuildMockClient(), "");
        Assert.NotEqual(0, hresult); // E_FAIL, cleanly - and the "Unknown command" line went to Output
        Assert.Equal("Unknown command 'does_not_exist'\n", s_captured);
    }
}
