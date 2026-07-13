using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WinDbgAotExt.Bridge
{
    // One managed string found on the heap: its object address and value. Plain fields so a script can
    // LINQ them (debugger.Strings(...)), and the command can format them.
    public sealed class StringHit
    {
        public ulong Address { get; init; }
        public string Value { get; init; } = "";
        public override string ToString() => $"  0x{Address:x}  \"{Value}\"";
    }

    // The PURE half of !strings: pattern compilation and listing formatting. Dependency-free (no ClrMD),
    // so it links into the test project like FieldRendering / WilTriage and every rule is unit-tested.
    // The heap WALK (Debugger.Strings) lives in Bridge.cs and is proven in cdb.
    public static class StringRendering
    {
        // A managed heap holds a LOT of strings; the command shows at most this many and reports the rest.
        public const int DefaultCap = 200;

        // Compile the operator's regex, or report why it won't. Empty/whitespace pattern = "match all"
        // (null regex). Returns false only on an INVALID pattern, so the caller can surface it instead of
        // throwing deep in the heap walk.
        public static bool TryCompilePattern(string? pattern, out Regex? regex, out string? error)
        {
            regex = null;
            error = null;
            if (string.IsNullOrWhiteSpace(pattern)) return true;   // match-all
            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                return true;
            }
            catch (ArgumentException argumentException)
            {
                error = "!strings: invalid regex — " + argumentException.Message;
                return false;
            }
        }

        // Render the listing the operator sees. `totalMatched` is how many strings matched BEFORE the cap,
        // so a truncated listing says how many were dropped (never silently hide the tail).
        public static string Format(List<StringHit> shown, int totalMatched, int cap, string? pattern)
        {
            string scope = string.IsNullOrWhiteSpace(pattern) ? "" : $" matching /{pattern}/";
            if (totalMatched == 0) return $"no managed strings{scope} found.";
            var builder = new StringBuilder();
            builder.AppendLine($"{totalMatched} managed string(s){scope}" + (totalMatched > shown.Count ? $" — showing first {shown.Count}:" : ":"));
            foreach (var hit in shown) builder.AppendLine(hit.ToString());
            if (totalMatched > shown.Count)
                builder.AppendLine($"  ... {totalMatched - shown.Count} more (raise the cap: !strings {(string.IsNullOrWhiteSpace(pattern) ? "" : pattern + " ")}--all, or narrow the pattern)");
            return builder.ToString().TrimEnd();
        }
    }
}
