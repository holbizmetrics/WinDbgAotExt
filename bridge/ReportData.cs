using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinDbgAotExt.Bridge
{
    // One row in the report's module table.
    public sealed class ReportModule
    {
        public string Name { get; init; } = "";
        public ulong Size { get; init; }
        public ulong Base { get; init; }
    }

    // One row in the report's managed-heap rollup (a type and its total footprint).
    public sealed class ReportHeapType
    {
        public string TypeName { get; init; } = "";
        public int Count { get; init; }
        public long Bytes { get; init; }
    }

    // Everything !report gathered, as plain data. Kept dependency-free (no ClrMD, no dbgeng) so the
    // markdown ASSEMBLY (ReportRendering.Build) links into the test project and is unit-tested; the
    // live GATHERING (Bridge.WriteReport) is proven in cdb.
    public sealed class ReportData
    {
        public string Generated { get; init; } = "";
        public string TargetKind { get; init; } = "";     // "crash dump" | "live process" | "unknown"
        public string VerTarget { get; init; } = "";       // raw `vertarget` output
        public string LastEventLine { get; init; } = "";
        public string TriageVerdict { get; init; } = "";
        public int ModuleCount { get; init; }
        public List<ReportModule> TopModules { get; init; } = new();
        public int ThreadCount { get; init; }
        public bool ClrPresent { get; init; }
        // null when the target is native (no managed heap); a list (possibly empty) when a CLR was found.
        public List<ReportHeapType>? TopHeapTypes { get; init; }
    }

    // Pure markdown assembler. Given the gathered ReportData, produce the report a junior engineer or an
    // AI can read without ever touching WinDbg — the whole point of the command.
    public static class ReportRendering
    {
        // Numbers are formatted with the INVARIANT culture, not the machine's: a report generated on a
        // German-locale box must read the same as one from a US box (and the same as the tests expect),
        // and a downstream AI/parser wants one predictable thousands separator.
        private static string Thousands(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string Thousands(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

        public static string Build(ReportData data)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# WinDbg triage report");
            builder.AppendLine();
            builder.AppendLine($"- **Generated:** {data.Generated}");
            builder.AppendLine($"- **Target:** {data.TargetKind}");
            builder.AppendLine($"- **Threads:** {data.ThreadCount}");
            builder.AppendLine($"- **Modules loaded:** {data.ModuleCount}");
            builder.AppendLine();

            builder.AppendLine("## Triage verdict");
            builder.AppendLine();
            builder.AppendLine(string.IsNullOrWhiteSpace(data.TriageVerdict) ? "_(none)_" : data.TriageVerdict);
            builder.AppendLine();

            builder.AppendLine("## Last event");
            builder.AppendLine();
            builder.AppendLine(string.IsNullOrWhiteSpace(data.LastEventLine) ? "_(none)_" : data.LastEventLine);
            builder.AppendLine();

            builder.AppendLine($"## Modules (top {data.TopModules.Count} of {data.ModuleCount} by size)");
            builder.AppendLine();
            builder.AppendLine("| Module | Size | Base |");
            builder.AppendLine("|---|---:|---|");
            foreach (var module in data.TopModules)
                builder.AppendLine($"| {module.Name} | {Thousands(module.Size)} | 0x{module.Base:x} |");
            builder.AppendLine();

            builder.AppendLine("## Managed heap");
            builder.AppendLine();
            if (!data.ClrPresent)
            {
                builder.AppendLine("_Native target — no managed (.NET) heap._");
            }
            else if (data.TopHeapTypes == null || data.TopHeapTypes.Count == 0)
            {
                builder.AppendLine("_Managed target, but no heap objects were read._");
            }
            else
            {
                builder.AppendLine($"Top {data.TopHeapTypes.Count} types by total bytes:");
                builder.AppendLine();
                builder.AppendLine("| Type | Count | Total bytes |");
                builder.AppendLine("|---|---:|---:|");
                foreach (var type in data.TopHeapTypes)
                    builder.AppendLine($"| {type.TypeName} | {Thousands(type.Count)} | {Thousands(type.Bytes)} |");
            }
            builder.AppendLine();

            builder.AppendLine("## Environment (`vertarget`)");
            builder.AppendLine();
            builder.AppendLine("```");
            builder.AppendLine(data.VerTarget.TrimEnd());
            builder.AppendLine("```");

            return builder.ToString().TrimEnd() + "\n";
        }
    }
}
