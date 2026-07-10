using System;
using System.Collections.Generic;
using System.Linq;

namespace WinDbgAotExt.Bridge
{
    // Pure break-triage classifier behind the !wiltriage command. Takes the text of `.lastevent` and `k`
    // (fetched by the caller via Debugger.Run) and returns a one-line verdict. Deliberately dependency-free
    // and side-effect-free: it compiles into the bridge for the runtime path AND is linked into the test
    // project, so every classification path is unit-tested without a live debugger.
    //
    // Honesty constraints from the TRIAD/KG audit that revised v1:
    //  - The exception code tells you the DELIVERY MECHANISM, not the MEANING. 0x80000003 is an int3
    //    (deliberate, never a hardware fault) but can still be a tripped assert / WIL check / heap-corruption
    //    break -- so a breakpoint is reported as "deliberate, process alive", NOT unconditionally "benign".
    //  - A first-chance access violation is routinely handled by the target; only a SECOND-chance one is a
    //    real (unhandled) fault. The chance is read from `.lastevent`, never assumed.
    //  - The culprit is the first non-framework frame's module -- a heuristic -- so the evidence
    //    (code, chance, innermost frame) is always appended for the operator to audit the call.
    public static class WilTriage
    {
        // Loader / CRT / COM / RPC / message-pump modules that are never themselves the reported culprit.
        private static readonly HashSet<string> FrameworkModules = new(StringComparer.OrdinalIgnoreCase)
        {
            "ntdll", "ntoskrnl", "kernel32", "kernelbase", "kernel", "combase", "ole32", "oleaut32",
            "rpcrt4", "ucrtbase", "msvcrt", "msvcp_win", "vcruntime140", "msvcp140", "user32", "win32u",
            "gdi32", "gdi32full", "advapi32", "sechost", "shcore", "shlwapi", "bcrypt", "bcryptprimitives",
            "wow64", "wow64cpu", "wow64win",
        };

        // Innermost-frame symbol fragments that mark an int3 which is REPORTING a failure, not just stopping.
        private static readonly string[] FailureBreakMarkers =
        {
            "RtlpBreakPointHeap", "RtlFailFast", "RtlReportFatalFailure", "DbgRaiseAssertionFailure",
            "_assert", "_wassert", "FailFast", "ReportFault", "__fastfail",
        };

        public static string Classify(string? lastEventText, string? stackText, bool stackFromException = false)
        {
            lastEventText ??= string.Empty;
            string exceptionCode = ExtractCode(lastEventText);
            string chance = ExtractChance(lastEventText);   // "1st" | "2nd" | "unknown"
            string culpritModule = FindCulprit(stackText, out string innermostFrame);

            string chanceLabel = exceptionCode.Length == 0 ? string.Empty
                : chance == "2nd" ? " 2nd-chance"
                : chance == "1st" ? " 1st-chance"
                : " chance-unknown";
            // stackFromException => the caller walked the stored exception context (.ecxr) instead of the
            // parked thread -- so the culprit is the real crash site; mark it so the operator knows.
            string frameSource = stackFromException ? " via .ecxr" : string.Empty;
            string evidence = $"[code={(exceptionCode.Length == 0 ? "?" : exceptionCode)}{chanceLabel}, top={innermostFrame}{frameSource}]";

            if (exceptionCode == "80000003")
            {
                bool looksLikeFailure = FailureBreakMarkers.Any(
                    marker => innermostFrame.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
                if (looksLikeFailure)
                    return $"DELIBERATE break in {culpritModule} that looks like a tripped assert/failfast -- investigate, do NOT blindly 'g'. {evidence}";
                return $"DELIBERATE int3 break in {culpritModule} -- no hardware fault, process is alive. Likely benign (WIL/loader/manual break) but may be a tripped assert/WIL check -- 'g' to continue, or decode the reason. {evidence}";
            }
            if (exceptionCode == "c0000005")
            {
                if (chance == "2nd")
                    return $"REAL FAULT: unhandled (2nd-chance) access violation in {culpritModule} -- run !analyze -v. {evidence}";
                if (chance == "1st")
                    return $"first-chance access violation in {culpritModule} -- often handled by the target; a real fault only if it reaches 2nd chance. {evidence}";
                return $"access violation in {culpritModule} -- chance not recorded (typical of a crash dump = the unhandled fault); run !analyze -v. {evidence}";
            }
            return $"unclassified break in {culpritModule} -- run !analyze -v. {evidence}";
        }

        // Pull the hex code following "code " in `.lastevent` (e.g. "... code 80000003 (first chance)").
        private static string ExtractCode(string lastEventText)
        {
            int codeIndex = lastEventText.IndexOf("code ", StringComparison.OrdinalIgnoreCase);
            if (codeIndex < 0) return string.Empty;
            int start = codeIndex + "code ".Length;
            int end = start;
            while (end < lastEventText.Length && Uri.IsHexDigit(lastEventText[end])) end++;
            return lastEventText.Substring(start, end - start).ToLowerInvariant();
        }

        // First/second chance from `.lastevent`. A crash dump renders "(first/second chance not available)"
        // -- return unknown, NOT second-chance: the old Contains("second chance") matched that substring
        // (the winvpnclient_cli crash-dump false-positive found by a cold test).
        private static string ExtractChance(string lastEventText)
        {
            if (lastEventText.IndexOf("not available", StringComparison.OrdinalIgnoreCase) >= 0) return "unknown";
            if (lastEventText.IndexOf("second chance", StringComparison.OrdinalIgnoreCase) >= 0) return "2nd";
            if (lastEventText.IndexOf("first chance", StringComparison.OrdinalIgnoreCase) >= 0) return "1st";
            return "unknown";
        }

        // First non-framework frame's module. innermostFrame is the innermost resolvable call site (evidence).
        private static string FindCulprit(string? stackText, out string innermostFrame)
        {
            innermostFrame = "?";
            if (string.IsNullOrEmpty(stackText)) return "unknown";
            bool innermostSet = false;
            foreach (string line in stackText.Split('\n'))
            {
                string? callSite = CallSiteToken(line);
                if (callSite == null) continue;
                if (!innermostSet) { innermostFrame = callSite; innermostSet = true; }
                int separator = callSite.IndexOfAny(new[] { '!', '+' });
                if (separator <= 0) continue;
                string module = callSite.Substring(0, separator);
                if (!FrameworkModules.Contains(module)) return module;
            }
            return "unknown";
        }

        // The call-site token of a `k` line: the "module!symbol" token if present, else a "module+offset"
        // token (never an address column, which carries the ` digit separator and no module name). Robust to
        // `.lines` ([file @ n]) and kb/kv arg columns, which the old "last token" rule broke on.
        private static string? CallSiteToken(string line)
        {
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string? withBang = tokens.FirstOrDefault(token => token.IndexOf('!') > 0);
            if (withBang != null) return withBang;
            return tokens.FirstOrDefault(
                token => token.IndexOf('+') > 0 && !token.Contains('`')
                         && !token.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
        }
    }
}
