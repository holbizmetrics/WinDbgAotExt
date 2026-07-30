using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinDbgAotExt.Bridge
{
    /// <summary>
    /// One instance field of an inspected object, projected to plain fields for LINQ + readable print.
    /// </summary>
    public sealed class FieldInfo
    {
        /// <summary>Field name as declared on the type.</summary>
        public string Name { get; init; } = "";

        /// <summary>Field type name (ClrMD notation).</summary>
        public string TypeName { get; init; } = "";

        /// <summary>Rendered value: primitives inline, strings quoted, references as addresses.</summary>
        public string Value { get; init; } = "";

        /// <summary>
        /// Nonzero only for a non-null object reference — the address to <c>!fields</c> into next.
        /// </summary>
        public ulong ObjectAddress { get; init; }

        /// <summary>A pseudo-field carrying a message to the operator (e.g. a read failure) instead of data.</summary>
        public static FieldInfo Note(string message) => new FieldInfo { Name = "(note)", Value = message };

        /// <summary>One listing line: <c>TypeName Name = Value</c>.</summary>
        public override string ToString() => $"  {TypeName} {Name} = {Value}";
    }

    /// <summary>
    /// The PURE half of <c>!fields</c>: address parsing and listing formatting. Dependency-free (no ClrMD,
    /// no Roslyn), so it links into the test project exactly like WilTriage / LastEventInfo and every
    /// notation + rendering rule is unit-tested without a live debugger. The ClrMD READ lives in
    /// <c>Debugger.Fields</c> (Bridge.cs) and is proven in cdb.
    /// </summary>
    public static class FieldRendering
    {
        /// <summary>
        /// Parse an operator-typed address. Accepts "0x1c4a...", bare hex "1c4a...", and WinDbg's
        /// backtick-grouped "0000001c`4a3b0010".
        /// </summary>
        /// <param name="text">The address text as typed; null-safe.</param>
        /// <param name="address">The parsed address when the method returns true.</param>
        /// <returns>True when the text parses as a hex address.</returns>
        public static bool TryParseAddress(string text, out ulong address)
        {
            string cleaned = (text ?? "").Trim().Replace("`", "");
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
            return ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out address);
        }

        /// <summary>
        /// Render the listing exactly as the operator sees it, including the drill-in hint for the first
        /// object reference.
        /// </summary>
        /// <param name="address">The inspected object's address (used only for the empty-listing message).</param>
        /// <param name="fields">The fields to render, in declaration order.</param>
        public static string FormatFields(ulong address, List<FieldInfo> fields)
        {
            if (fields.Count == 0) return $"0x{address:x} has no readable instance fields.";
            var builder = new StringBuilder();
            foreach (var field in fields) builder.AppendLine(field.ToString());
            var firstReference = fields.FirstOrDefault(f => f.ObjectAddress != 0);
            if (firstReference != null)
                builder.AppendLine($"  (drill in: !fields 0x{firstReference.ObjectAddress:x})");
            return builder.ToString().TrimEnd();
        }
    }
}
