using System;

namespace WinDbgAotExt.Bridge
{
    // Typed "last debugger event" -- the structured replacement for parsing `.lastevent` text.
    // Produced by Debugger.LastEvent (IDebugControl::GetLastEventInformation, vtable slot 94) and
    // decoded here from the raw ExtraInformation buffer. The decode is PURE and dependency-free:
    // the byte offsets -- where interop bugs actually live in this repo's history (Output 8->14,
    // the chance-substring false positive) -- compile into the test project and are locked by unit
    // tests against hand-built buffers, no live debugger needed.
    public sealed class LastEventInfo
    {
        public const uint DEBUG_EVENT_BREAKPOINT = 0x1;
        public const uint DEBUG_EVENT_EXCEPTION = 0x2;

        // DEBUG_EVENT_* bit for the event (dbgeng.h): 0x1 breakpoint, 0x2 exception, ...
        public uint EventType { get; init; }
        public uint ProcessId { get; init; }
        public uint ThreadId { get; init; }
        // dbgeng's own one-line rendering (e.g. "Access violation - code c0000005 (...)"): kept as
        // evidence for the operator, never parsed.
        public string Description { get; init; } = "";

        public bool IsBreakpoint => EventType == DEBUG_EVENT_BREAKPOINT;
        public bool IsException => EventType == DEBUG_EVENT_EXCEPTION;

        // Valid only when IsException (decoded from DEBUG_LAST_EVENT_INFO_EXCEPTION):
        public uint ExceptionCode { get; init; }
        public ulong ExceptionAddress { get; init; }
        // Raw FirstChance ULONG from dbgeng (nonzero = first chance). On a DUMP target the stored
        // value carries no live chance semantics -- use Chance, which folds that in.
        public uint FirstChanceRaw { get; init; }
        // True when the target is a dump (GetDebuggeeType qualifier >= DEBUG_DUMP_SMALL). Matches
        // `.lastevent`'s "(first/second chance not available)" rendering on dumps.
        public bool IsDumpTarget { get; init; }

        // "1st" | "2nd" | "unknown" -- the exact vocabulary WilTriage.Classify already speaks.
        public string Chance =>
            !IsException ? "unknown"
            : IsDumpTarget ? "unknown"
            : FirstChanceRaw != 0 ? "1st"
            : "2nd";

        // DEBUG_LAST_EVENT_INFO_EXCEPTION = EXCEPTION_RECORD64 + ULONG FirstChance (dbgeng.h).
        // EXCEPTION_RECORD64 (winnt.h 10.0.26100.0): Code@0, Flags@4, Record@8, Address@16,
        // NumberParameters@24, pad@28, Information[15]@32 -> struct ends @152; FirstChance@152.
        private const int ExceptionCodeOffset = 0;
        private const int ExceptionAddressOffset = 16;
        private const int FirstChanceOffset = 152;
        public const int ExceptionExtraInformationSize = 156;

        public static LastEventInfo Decode(
            uint eventType, uint processId, uint threadId, string description,
            ReadOnlySpan<byte> extraInformation, bool isDumpTarget)
        {
            bool hasExceptionRecord =
                eventType == DEBUG_EVENT_EXCEPTION
                && extraInformation.Length >= ExceptionExtraInformationSize;
            return new LastEventInfo
            {
                EventType = eventType,
                ProcessId = processId,
                ThreadId = threadId,
                Description = description,
                IsDumpTarget = isDumpTarget,
                ExceptionCode = hasExceptionRecord ? ReadU32(extraInformation, ExceptionCodeOffset) : 0,
                ExceptionAddress = hasExceptionRecord ? ReadU64(extraInformation, ExceptionAddressOffset) : 0,
                FirstChanceRaw = hasExceptionRecord ? ReadU32(extraInformation, FirstChanceOffset) : 0,
            };
        }

        private static uint ReadU32(ReadOnlySpan<byte> buffer, int offset) =>
            BitConverter.ToUInt32(buffer.Slice(offset, 4));

        private static ulong ReadU64(ReadOnlySpan<byte> buffer, int offset) =>
            BitConverter.ToUInt64(buffer.Slice(offset, 8));

        public override string ToString() =>
            IsException
                ? $"exception {ExceptionCode:x8} ({Chance} chance) at 0x{ExceptionAddress:x}"
                : $"event type 0x{EventType:x} -- {Description}";
    }
}
