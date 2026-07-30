using System;

namespace WinDbgAotExt.Bridge
{
    /// <summary>
    /// Typed "last debugger event" — the structured replacement for parsing <c>.lastevent</c> text.
    /// Produced by <c>Debugger.LastEvent</c> (IDebugControl::GetLastEventInformation, vtable slot 94) and
    /// decoded here from the raw ExtraInformation buffer. The decode is PURE and dependency-free:
    /// the byte offsets — where interop bugs actually live in this repo's history (Output 8-&gt;14,
    /// the chance-substring false positive) — compile into the test project and are locked by unit
    /// tests against hand-built buffers, no live debugger needed.
    /// </summary>
    public sealed class LastEventInfo
    {
        /// <summary>DEBUG_EVENT_* bit for a breakpoint event (dbgeng.h).</summary>
        public const uint DEBUG_EVENT_BREAKPOINT = 0x1;

        /// <summary>DEBUG_EVENT_* bit for an exception event (dbgeng.h).</summary>
        public const uint DEBUG_EVENT_EXCEPTION = 0x2;

        /// <summary>DEBUG_EVENT_* bit for the event (dbgeng.h): 0x1 breakpoint, 0x2 exception, ...</summary>
        public uint EventType { get; init; }

        /// <summary>Engine process id the event occurred in.</summary>
        public uint ProcessId { get; init; }

        /// <summary>Engine thread id the event occurred on.</summary>
        public uint ThreadId { get; init; }

        /// <summary>
        /// dbgeng's own one-line rendering (e.g. "Access violation - code c0000005 (...)"): kept as
        /// evidence for the operator, never parsed.
        /// </summary>
        public string Description { get; init; } = "";

        /// <summary>True when the event is a breakpoint.</summary>
        public bool IsBreakpoint => EventType == DEBUG_EVENT_BREAKPOINT;

        /// <summary>True when the event is an exception.</summary>
        public bool IsException => EventType == DEBUG_EVENT_EXCEPTION;

        /// <summary>Exception code; valid only when <see cref="IsException"/> (decoded from DEBUG_LAST_EVENT_INFO_EXCEPTION).</summary>
        public uint ExceptionCode { get; init; }

        /// <summary>Faulting address; valid only when <see cref="IsException"/>.</summary>
        public ulong ExceptionAddress { get; init; }

        /// <summary>
        /// Raw FirstChance ULONG from dbgeng (nonzero = first chance). On a DUMP target the stored
        /// value carries no live chance semantics — use <see cref="Chance"/>, which folds that in.
        /// </summary>
        public uint FirstChanceRaw { get; init; }

        /// <summary>
        /// True when the target is a dump (GetDebuggeeType qualifier &gt;= DEBUG_DUMP_SMALL), false when
        /// live, NULL when the query itself failed. The null case is load-bearing: an UNKNOWN target
        /// kind must NOT be assumed live, or a dump's stored FirstChance=0 reads as a real 2nd-chance
        /// fault — resurrecting on the error path the exact false positive this typed path exists to
        /// kill (the winvpnclient_cli cold-dump class).
        /// </summary>
        public bool? IsDumpTarget { get; init; }

        /// <summary>
        /// False when no exception record was actually decoded (wrong event type, or a buffer shorter
        /// than the record). Distinguishes "read as second chance" from "never read at all" — without
        /// it a zeroed/short buffer silently reports 2nd-chance.
        /// </summary>
        public bool ExceptionRecordDecoded { get; init; }

        /// <summary>
        /// "1st" | "2nd" | "unknown" — the exact vocabulary <c>WilTriage.Classify</c> already speaks.
        /// EVERY uncertainty (not an exception / record not decoded / dump / unknown target kind)
        /// funnels to "unknown": the honest answer, and the one that does not accuse the target of a
        /// fault it may not have committed.
        /// </summary>
        public string Chance =>
            !IsException || !ExceptionRecordDecoded ? "unknown"
            : IsDumpTarget != false ? "unknown"
            : FirstChanceRaw != 0 ? "1st"
            : "2nd";

        // DEBUG_LAST_EVENT_INFO_EXCEPTION = EXCEPTION_RECORD64 + ULONG FirstChance (dbgeng.h).
        // EXCEPTION_RECORD64 (winnt.h 10.0.26100.0): Code@0, Flags@4, Record@8, Address@16,
        // NumberParameters@24, pad@28, Information[15]@32 -> struct ends @152; FirstChance@152.
        private const int ExceptionCodeOffset = 0;
        private const int ExceptionAddressOffset = 16;
        private const int FirstChanceOffset = 152;

        /// <summary>
        /// MINIMUM bytes that must be present to decode the record — NOT the C sizeof, which pads to
        /// 160 (152 + 4, rounded to 8-byte alignment). dbgeng may legitimately report 160 used; we
        /// require &gt;= this and clamp. Never use this as the size of a buffer you WRITE.
        /// </summary>
        public const int ExceptionExtraInformationSize = 156;

        /// <summary>
        /// Decode the raw GetLastEventInformation results into the typed event. Pure — every branch is
        /// unit-tested against hand-built buffers.
        /// </summary>
        /// <param name="eventType">DEBUG_EVENT_* bit from dbgeng.</param>
        /// <param name="processId">Engine process id.</param>
        /// <param name="threadId">Engine thread id.</param>
        /// <param name="description">dbgeng's one-line event description (kept verbatim).</param>
        /// <param name="extraInformation">The raw ExtraInformation buffer; the exception record is decoded
        /// only when the event is an exception AND the buffer is at least
        /// <see cref="ExceptionExtraInformationSize"/> bytes.</param>
        /// <param name="isDumpTarget">Target kind per GetDebuggeeType: true = dump, false = live,
        /// null = query failed (propagates into <see cref="Chance"/> as "unknown").</param>
        public static LastEventInfo Decode(
            uint eventType, uint processId, uint threadId, string description,
            ReadOnlySpan<byte> extraInformation, bool? isDumpTarget)
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
                ExceptionRecordDecoded = hasExceptionRecord,
                ExceptionCode = hasExceptionRecord ? ReadU32(extraInformation, ExceptionCodeOffset) : 0,
                ExceptionAddress = hasExceptionRecord ? ReadU64(extraInformation, ExceptionAddressOffset) : 0,
                FirstChanceRaw = hasExceptionRecord ? ReadU32(extraInformation, FirstChanceOffset) : 0,
            };
        }

        private static uint ReadU32(ReadOnlySpan<byte> buffer, int offset) =>
            BitConverter.ToUInt32(buffer.Slice(offset, 4));

        private static ulong ReadU64(ReadOnlySpan<byte> buffer, int offset) =>
            BitConverter.ToUInt64(buffer.Slice(offset, 8));

        /// <summary>One line for the operator: exception code + chance + address, or event type + description.</summary>
        public override string ToString() =>
            IsException
                ? $"exception {ExceptionCode:x8} ({Chance} chance) at 0x{ExceptionAddress:x}"
                : $"event type 0x{EventType:x} -- {Description}";
    }
}
