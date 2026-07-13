using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinDbgAotExt.Bridge
{
    // One instance field of an inspected object, projected to plain fields for LINQ + readable print.
    // ObjectAddress is nonzero only for a non-null object reference — the address to `!fields` into next.
    public sealed class FieldInfo
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string Value { get; init; } = "";
        public ulong ObjectAddress { get; init; }
        public static FieldInfo Note(string message) => new FieldInfo { Name = "(note)", Value = message };
        public override string ToString() => $"  {TypeName} {Name} = {Value}";
    }

    // The PURE half of !fields: address parsing and listing formatting. Dependency-free (no ClrMD, no
    // Roslyn), so it links into the test project exactly like WilTriage / LastEventInfo and every
    // notation + rendering rule is unit-tested without a live debugger. The ClrMD READ lives in
    // Debugger.Fields (Bridge.cs) and is proven in cdb.
    public static class FieldRendering
    {
        // Accept "0x1c4a...", bare hex "1c4a...", and WinDbg's `-grouped "0000001c`4a3b0010".
        public static bool TryParseAddress(string text, out ulong address)
        {
            string cleaned = (text ?? "").Trim().Replace("`", "");
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned.Substring(2);
            return ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out address);
        }

        // Render the listing exactly as the operator sees it, including the drill-in hint for the first
        // object reference.
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
