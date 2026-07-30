using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinDbgAotExt.Bridge
{
    /// <summary>One row in the report's module table.</summary>
    public sealed class ReportModule
    {
        /// <summary>Module name (without path).</summary>
        public string Name { get; init; } = "";

        /// <summary>Image size in bytes.</summary>
        public ulong Size { get; init; }

        /// <summary>Image base address.</summary>
        public ulong Base { get; init; }
    }

    /// <summary>One row in the report's managed-heap rollup (a type and its total footprint).</summary>
    public sealed class ReportHeapType
    {
        /// <summary>Fully-qualified managed type name.</summary>
        public string TypeName { get; init; } = "";

        /// <summary>How many instances were counted on the heap.</summary>
        public int Count { get; init; }

        /// <summary>Total bytes across all counted instances.</summary>
        public long Bytes { get; init; }
    }

    /// <summary>
    /// Everything <c>!report</c> gathered, as plain data. Kept dependency-free (no ClrMD, no dbgeng) so
    /// the markdown ASSEMBLY (<see cref="ReportRendering.Build"/>) links into the test project and is
    /// unit-tested; the live GATHERING (<c>Bridge.WriteReport</c>) is proven in cdb.
    /// </summary>
    public sealed class ReportData
    {
        /// <summary>Generation timestamp, preformatted by the gatherer.</summary>
        public string Generated { get; init; } = "";

        /// <summary>"crash dump" | "live process" | "unknown".</summary>
        public string TargetKind { get; init; } = "";

        /// <summary>Raw <c>vertarget</c> output, echoed verbatim in the report.</summary>
        public string VerTarget { get; init; } = "";

        /// <summary>The one-line last-event rendering.</summary>
        public string LastEventLine { get; init; } = "";

        /// <summary>The <c>!wiltriage</c> verdict for the current break.</summary>
        public string TriageVerdict { get; init; } = "";

        /// <summary>Total number of loaded modules (the table below shows only the top N).</summary>
        public int ModuleCount { get; init; }

        /// <summary>The largest modules by image size, already selected and ordered by the gatherer.</summary>
        public List<ReportModule> TopModules { get; init; } = new();

        /// <summary>Number of threads in the target.</summary>
        public int ThreadCount { get; init; }

        /// <summary>True when a CLR was found in the target.</summary>
        public bool ClrPresent { get; init; }

        /// <summary>
        /// Null when the target is native (no managed heap); a list (possibly empty) when a CLR was found.
        /// </summary>
        public List<ReportHeapType>? TopHeapTypes { get; init; }
    }

    /// <summary>
    /// Pure markdown assembler. Given the gathered <see cref="ReportData"/>, produce the report a junior
    /// engineer or an AI can read without ever touching WinDbg — the whole point of the command.
    /// </summary>
    public static class ReportRendering
    {
        // Numbers are formatted with the INVARIANT culture, not the machine's: a report generated on a
        // German-locale box must read the same as one from a US box (and the same as the tests expect),
        // and a downstream AI/parser wants one predictable thousands separator.
        private static string Thousands(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
        private static string Thousands(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>
        /// Assemble the full markdown report from the gathered data. Pure and deterministic — numbers use
        /// the invariant culture so the same data yields byte-identical markdown on any locale.
        /// </summary>
        /// <param name="data">The gathered report data.</param>
        /// <returns>The complete markdown document, ending in exactly one newline.</returns>
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
