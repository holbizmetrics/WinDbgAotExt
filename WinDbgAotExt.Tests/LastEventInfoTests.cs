using System;
using WinDbgAotExt.Bridge;
using Xunit;

namespace WinDbgAotExt.Tests;

// Locks the DEBUG_LAST_EVENT_INFO_EXCEPTION byte offsets (ExceptionCode@0, ExceptionAddress@16,
// FirstChance@152 -- verified against winnt.h/dbgeng.h 10.0.26100.0) and the chance semantics that
// replace `.lastevent` substring parsing. Buffers are hand-built exactly as dbgeng would fill them,
// so a wrong offset in the decoder fails HERE, not in a live cdb session.
public class LastEventInfoTests
{
    // Build a DEBUG_LAST_EVENT_INFO_EXCEPTION buffer the way dbgeng lays it out.
    private static byte[] ExceptionExtraInformation(uint exceptionCode, ulong exceptionAddress, uint firstChance)
    {
        byte[] buffer = new byte[LastEventInfo.ExceptionExtraInformationSize];
        BitConverter.GetBytes(exceptionCode).CopyTo(buffer, 0);     // EXCEPTION_RECORD64.ExceptionCode
        BitConverter.GetBytes(exceptionAddress).CopyTo(buffer, 16); // EXCEPTION_RECORD64.ExceptionAddress
        BitConverter.GetBytes(firstChance).CopyTo(buffer, 152);     // DEBUG_LAST_EVENT_INFO_EXCEPTION.FirstChance
        return buffer;
    }

    [Fact]
    public void Decode_UnpacksExceptionCodeAddressAndFirstChance()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, processId: 1234, threadId: 5678,
            description: "Access violation - code c0000005",
            ExceptionExtraInformation(0xC0000005, 0x00007FF6_12345678, firstChance: 1),
            isDumpTarget: false);

        Assert.True(lastEvent.IsException);
        Assert.False(lastEvent.IsBreakpoint);
        Assert.Equal(0xC0000005u, lastEvent.ExceptionCode);
        Assert.Equal(0x00007FF6_12345678ul, lastEvent.ExceptionAddress);
        Assert.Equal(1u, lastEvent.FirstChanceRaw);
        Assert.Equal(1234u, lastEvent.ProcessId);
        Assert.Equal(5678u, lastEvent.ThreadId);
    }

    [Fact]
    public void Chance_LiveFirstChance_Is1st()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0, firstChance: 1), isDumpTarget: false);
        Assert.Equal("1st", lastEvent.Chance);
    }

    [Fact]
    public void Chance_LiveSecondChance_Is2nd()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0, firstChance: 0), isDumpTarget: false);
        Assert.Equal("2nd", lastEvent.Chance);
    }

    [Fact]
    public void Chance_DumpTarget_IsUnknown_EvenWhenFirstChanceBitIsSet()
    {
        // A dump stores SOME FirstChance value, but it carries no live semantics -- the typed path
        // must say "unknown" exactly like `.lastevent`'s "(first/second chance not available)".
        // This is the typed twin of the winvpnclient_cli cold-dump false-positive.
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0, firstChance: 1), isDumpTarget: true);
        Assert.Equal("unknown", lastEvent.Chance);
    }

    [Fact]
    public void Decode_BreakpointEvent_HasNoExceptionFields()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_BREAKPOINT, 0, 0, "Breakpoint 0 hit",
            ReadOnlySpan<byte>.Empty, isDumpTarget: false);
        Assert.True(lastEvent.IsBreakpoint);
        Assert.False(lastEvent.IsException);
        Assert.Equal(0u, lastEvent.ExceptionCode);
        Assert.Equal("unknown", lastEvent.Chance);
    }

    [Fact]
    public void Decode_ShortExceptionBuffer_DoesNotThrowAndYieldsNoFields()
    {
        // dbgeng reported an exception event but the extra-information buffer came back truncated:
        // decode must degrade to zeroed fields, never read past the span.
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            new byte[8], isDumpTarget: false);
        Assert.True(lastEvent.IsException);
        Assert.False(lastEvent.ExceptionRecordDecoded);
        Assert.Equal(0u, lastEvent.ExceptionCode);
        Assert.Equal(0ul, lastEvent.ExceptionAddress);
    }

    // --- the "uncertainty must never read as a fault" contract (audit: converged MED) ---
    // Two arms found the same hole: a chance that was never READ decoded to FirstChanceRaw=0, which
    // the old ternary reported as "2nd" -- i.e. "unhandled REAL FAULT". That is the winvpnclient_cli
    // false positive resurrected on the error path, in the very code written to kill it. Every
    // uncertainty must funnel to "unknown".

    [Fact]
    public void Chance_TruncatedRecord_IsUnknown_NotSecondChance()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            new byte[8], isDumpTarget: false);   // record never decoded
        Assert.Equal("unknown", lastEvent.Chance);
    }

    [Fact]
    public void Chance_UnknownTargetKind_IsUnknown_NotSecondChance()
    {
        // GetDebuggeeType failed -> IsDumpTarget is null. A dump's stored FirstChance=0 must NOT be
        // read as a live 2nd-chance fault just because we couldn't tell what kind of target it is.
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0x1000, firstChance: 0), isDumpTarget: null);
        Assert.Equal("unknown", lastEvent.Chance);
    }

    [Fact]
    public void ClassifyTyped_UnknownTargetKind_DoesNotAccuseARealFault()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0x1000, firstChance: 0), isDumpTarget: null);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.DoesNotContain("REAL FAULT", verdict);
        Assert.Contains("chance not recorded", verdict);
    }

    [Fact]
    public void ClassifyTyped_Wow64Int3_IsDeliberate_NotUnclassified()
    {
        // STATUS_WX86_BREAKPOINT (0x4000001f): the same DebugBreak(), raised from 32-bit code under
        // WOW64. It used to fall through to "unclassified break -- run !analyze -v".
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0x4000001F, 0x2000, firstChance: 1), isDumpTarget: false);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.Contains("DELIBERATE", verdict);
        Assert.DoesNotContain("unclassified", verdict);
    }

    [Fact]
    public void Decode_ExceptionBufferAtCSizeof160_DecodesTheSameAs156()
    {
        // dbgeng may report 160 bytes used (the C sizeof pads 152+4 to 8-byte alignment); the decoder
        // requires >= 156 and must read the same fields from the padded buffer.
        byte[] padded = new byte[160];
        ExceptionExtraInformation(0xC0000005, 0xDEAD, firstChance: 1).CopyTo(padded, 0);
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "", padded, isDumpTarget: false);
        Assert.True(lastEvent.ExceptionRecordDecoded);
        Assert.Equal(0xC0000005u, lastEvent.ExceptionCode);
        Assert.Equal(0xDEADul, lastEvent.ExceptionAddress);
        Assert.Equal("1st", lastEvent.Chance);
    }

    // --- Typed Classify: the same verdicts the text path produces, from structured input ---

    private const string UserFaultStack =
        "00 00000000`0014f000 00007ff6`00001000 winvpnclient_cli!Connect+0x42\n" +
        "01 00000000`0014f100 00007ff8`00002000 kernel32!BaseThreadInitThunk+0x14\n";

    [Fact]
    public void ClassifyTyped_SecondChanceAccessViolation_IsRealFault()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0x1000, firstChance: 0), isDumpTarget: false);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.StartsWith("REAL FAULT", verdict);
        Assert.Contains("winvpnclient_cli", verdict);
        Assert.Contains("2nd-chance", verdict);
    }

    [Fact]
    public void ClassifyTyped_DumpAccessViolation_ReportsChanceNotRecorded()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0x1000, firstChance: 1), isDumpTarget: true);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.Contains("chance not recorded", verdict);
        Assert.Contains("chance-unknown", verdict);
    }

    [Fact]
    public void ClassifyTyped_Int3Exception_IsDeliberateBreak()
    {
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0x80000003, 0x2000, firstChance: 1), isDumpTarget: false);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.Contains("DELIBERATE", verdict);
    }

    [Fact]
    public void ClassifyTyped_BreakpointEvent_ClassifiesDownTheDeliberatePath()
    {
        // A debugger-set breakpoint arrives as DEBUG_EVENT_BREAKPOINT (no exception record); it is
        // deliberate by definition and must not land in the unclassified bucket.
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_BREAKPOINT, 0, 0, "Breakpoint 0 hit",
            ReadOnlySpan<byte>.Empty, isDumpTarget: false);
        string verdict = WilTriage.Classify(lastEvent, UserFaultStack);
        Assert.Contains("DELIBERATE", verdict);
    }

    [Fact]
    public void ClassifyTyped_MatchesTextPath_OnTheSameSecondChanceAv()
    {
        // The typed and text paths must speak with one voice: same event, same verdict.
        var lastEvent = LastEventInfo.Decode(
            LastEventInfo.DEBUG_EVENT_EXCEPTION, 0, 0, "",
            ExceptionExtraInformation(0xC0000005, 0x1000, firstChance: 0), isDumpTarget: false);
        string typedVerdict = WilTriage.Classify(lastEvent, UserFaultStack);
        string textVerdict = WilTriage.Classify(
            "Last event: Access violation - code c0000005 (second chance)", UserFaultStack);
        Assert.Equal(textVerdict, typedVerdict);
    }
}
