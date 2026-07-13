using System.Collections.Generic;
using WinDbgAotExt.Bridge;
using Xunit;

namespace WinDbgAotExt.Tests;

// The PURE seam of !report: assembling the markdown from gathered data. The live GATHERING (running
// commands, ClrMD heap walk, file write) needs a real target and is proven in cdb. These lock the
// document shape a downstream consumer (junior engineer / AI) depends on.
public class ReportTests
{
    private static ReportData SampleManaged() => new ReportData
    {
        Generated = "2026-07-13 15:00:00",
        TargetKind = "crash dump",
        VerTarget = "Windows 10 Version 26100\nProcess Uptime: 0 days 0:23:02",
        LastEventLine = "exception c0000005 (2nd chance) at 0x7ff600001000",
        TriageVerdict = "REAL FAULT: unhandled (2nd-chance) access violation in myapp -- run !analyze -v.",
        ModuleCount = 42,
        TopModules = new List<ReportModule>
        {
            new ReportModule { Name = "coreclr", Size = 6_000_000, Base = 0x7ff600000000 },
            new ReportModule { Name = "myapp",   Size = 1_200_000, Base = 0x7ff610000000 },
        },
        ThreadCount = 7,
        ClrPresent = true,
        TopHeapTypes = new List<ReportHeapType>
        {
            new ReportHeapType { TypeName = "System.Byte[]", Count = 50, Bytes = 204_800 },
            new ReportHeapType { TypeName = "System.String", Count = 1200, Bytes = 96_000 },
        },
    };

    [Fact]
    public void Build_HasAllSections_InReadableMarkdown()
    {
        string report = ReportRendering.Build(SampleManaged());

        Assert.Contains("# WinDbg triage report", report);
        Assert.Contains("**Target:** crash dump", report);
        Assert.Contains("**Threads:** 7", report);
        Assert.Contains("## Triage verdict", report);
        Assert.Contains("REAL FAULT", report);
        Assert.Contains("## Last event", report);
        Assert.Contains("## Modules (top 2 of 42 by size)", report);
        Assert.Contains("| coreclr | 6,000,000 | 0x7ff600000000 |", report);
        Assert.Contains("## Managed heap", report);
        Assert.Contains("| System.Byte[] | 50 | 204,800 |", report);
        Assert.Contains("## Environment (`vertarget`)", report);
        Assert.Contains("Windows 10 Version 26100", report);
    }

    [Fact]
    public void Build_NativeTarget_SaysNoManagedHeap_NotAnEmptyTable()
    {
        var native = new ReportData
        {
            Generated = "2026-07-13 15:00:00",
            TargetKind = "live process",
            VerTarget = "Windows 10",
            LastEventLine = "exception 80000003 (1st chance)",
            TriageVerdict = "DELIBERATE int3 break in ntdll",
            ModuleCount = 10,
            TopModules = new List<ReportModule> { new ReportModule { Name = "ntdll", Size = 2_000_000, Base = 0x7ff9_00000000 } },
            ThreadCount = 3,
            ClrPresent = false,
            TopHeapTypes = null,
        };

        string report = ReportRendering.Build(native);
        Assert.Contains("Native target — no managed (.NET) heap.", report);
        Assert.DoesNotContain("Total bytes", report);   // no heap table emitted
    }

    [Fact]
    public void Build_EndsWithSingleTrailingNewline()
    {
        string report = ReportRendering.Build(SampleManaged());
        Assert.EndsWith("```\n", report);          // fenced vertarget block, one trailing newline
        Assert.DoesNotContain("\n\n\n", report);   // no runaway blank runs
    }
}
