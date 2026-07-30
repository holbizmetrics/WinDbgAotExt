using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WinDbgAotExt.Bridge
{
    /// <summary>
    /// One managed string found on the heap: its object address and value. Plain fields so a script can
    /// LINQ them (<c>debugger.Strings(...)</c>), and the command can format them.
    /// </summary>
    public sealed class StringHit
    {
        /// <summary>Object address of the string on the managed heap.</summary>
        public ulong Address { get; init; }

        /// <summary>The string's value.</summary>
        public string Value { get; init; } = "";

        /// <summary>One listing line: <c>0xADDR  "value"</c>.</summary>
        public override string ToString() => $"  0x{Address:x}  \"{Value}\"";
    }

    /// <summary>
    /// The PURE half of <c>!strings</c>: pattern compilation and listing formatting. Dependency-free
    /// (no ClrMD), so it links into the test project like FieldRendering / WilTriage and every rule is
    /// unit-tested. The heap WALK (<c>Debugger.Strings</c>) lives in Bridge.cs and is proven in cdb.
    /// </summary>
    public static class StringRendering
    {
        /// <summary>
        /// A managed heap holds a LOT of strings; the command shows at most this many and reports the rest.
        /// </summary>
        public const int DefaultCap = 200;

        /// <summary>
        /// Compile the operator's regex, or report why it won't. Empty/whitespace pattern = "match all"
        /// (null regex).
        /// </summary>
        /// <param name="pattern">The operator's pattern; empty or whitespace means match-all.</param>
        /// <param name="regex">The compiled regex, or null for match-all.</param>
        /// <param name="error">The operator-facing message when the pattern is invalid.</param>
        /// <returns>
        /// False only on an INVALID pattern, so the caller can surface it instead of throwing deep in
        /// the heap walk.
        /// </returns>
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

        /// <summary>
        /// Render the listing the operator sees. A truncated listing always says how many strings were
        /// dropped (never silently hide the tail).
        /// </summary>
        /// <param name="shown">The hits actually rendered (already capped).</param>
        /// <param name="totalMatched">How many strings matched BEFORE the cap, so the truncation note is honest.</param>
        /// <param name="cap">The cap that was applied (part of the operator's mental model, not re-derived).</param>
        /// <param name="pattern">The pattern used, echoed in the header; null/empty means unfiltered.</param>
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
