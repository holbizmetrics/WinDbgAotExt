using WinDbgAotExt.Bridge;
using Xunit;

namespace WinDbgAotExt.Tests;

// Golden tests for the break-triage classifier. These pin the behaviours the TRIAD/KG audit revised v1 to:
// a breakpoint is reported "deliberate", never unconditionally "benign"; a failure-marker int3 is flagged;
// first-chance vs second-chance AVs are distinguished; the culprit is the first non-framework module; and
// the evidence line is always present. All run without a live debugger (Debugger.Run's outputs are fixtures).
public class WilTriageTests
{
    // Realistic `k` output (address columns with the ` digit separator), as Debugger.Run("k") returns it.
    private const string LoaderStack =
        "Child-SP          RetAddr               Call Site\n" +
        "0000001b`ab39ef60 00007ffa`0928d83a     ntdll!LdrpDoDebuggerBreak+0x35\n" +
        "0000001b`ab39efa0 00007ffa`0928ba50     ntdll!LdrpInitializeProcess+0x1ae6\n" +
        "0000001b`ab39f4a0 00000000`00000000     ntdll!LdrInitializeThunk+0xe";

    private const string WslWilStack =
        "Child-SP          RetAddr               Call Site\n" +
        "000000df`36cfee88 00007ff9`49512e84     KERNELBASE!wil::details::DebugBreak+0x2\n" +
        "000000df`36cfee90 00007ff9`4952ddf7     wsldevicehost+0xd2e84\n" +
        "000000df`36cffbb0 00007ffa`09287c1c     KERNEL32!BaseThreadInitThunk+0x17\n" +
        "000000df`36cffbe0 00000000`00000000     ntdll!RtlUserThreadStart+0x2c";

    private const string HeapCorruptStack =
        "000000df`0000aa00 00007ffa`00000001     ntdll!RtlpBreakPointHeap+0x1\n" +
        "000000df`0000aa40 00007ffa`00000002     myapp!AllocThing+0x42";

    private const string AppAvStack =
        "000000df`0000bb00 00007ffa`00000003     myapp!Crash+0x10\n" +
        "000000df`0000bb40 00007ffa`00000004     KERNEL32!BaseThreadInitThunk+0x17";

    [Fact]
    public void LoaderBreak_IsDeliberate_NotFault_AndNotOverclaimedBenign()
    {
        string verdict = WilTriage.Classify(
            "Last event: 1.2: Break instruction exception - code 80000003 (first chance)", LoaderStack);
        Assert.Contains("DELIBERATE", verdict);
        Assert.DoesNotContain("REAL FAULT", verdict);
        Assert.DoesNotContain("no crash occurred", verdict); // the v1 overclaim must not come back
    }

    [Fact]
    public void WslWilBreak_NamesWsldevicehostAsCulprit()
    {
        string verdict = WilTriage.Classify("... code 80000003 (first chance)", WslWilStack);
        Assert.Contains("wsldevicehost", verdict);
        Assert.Contains("DELIBERATE", verdict);
    }

    [Fact]
    public void HeapCorruptionInt3_IsFlaggedForInvestigation_NotBenign()
    {
        string verdict = WilTriage.Classify("... code 80000003 (first chance)", HeapCorruptStack);
        Assert.Contains("investigate", verdict);
        Assert.Contains("myapp", verdict); // culprit is the app frame, past ntdll!RtlpBreakPointHeap
    }

    [Fact]
    public void FirstChanceAccessViolation_IsNotCalledRealFault()
    {
        string verdict = WilTriage.Classify("... code c0000005 (first chance)", AppAvStack);
        Assert.DoesNotContain("REAL FAULT", verdict);
        Assert.Contains("first-chance", verdict);
        Assert.Contains("myapp", verdict);
    }

    [Fact]
    public void SecondChanceAccessViolation_IsRealFault()
    {
        string verdict = WilTriage.Classify("... code c0000005 (second chance)", AppAvStack);
        Assert.Contains("REAL FAULT", verdict);
        Assert.Contains("myapp", verdict);
    }

    [Fact]
    public void DumpChanceNotAvailable_IsNotFalselyLabelledSecondChance()
    {
        // A crash dump renders "(first/second chance not available)"; the old Contains("second chance")
        // matched that substring (the winvpnclient_cli AV dump, found by a cold test). Must read
        // chance-unknown, not 2nd-chance — and never claim a chance the dump didn't record.
        string verdict = WilTriage.Classify(
            "Last event: 18c8.5b60: Access violation - code c0000005 (first/second chance not available)", AppAvStack);
        Assert.DoesNotContain("2nd-chance", verdict);
        Assert.DoesNotContain("first-chance", verdict);
        Assert.Contains("chance-unknown", verdict);
        Assert.Contains("!analyze -v", verdict);
    }

    [Fact]
    public void UnknownCode_FallsBackToAnalyze()
    {
        string verdict = WilTriage.Classify("... code e0434352 (first chance)", AppAvStack);
        Assert.Contains("unclassified", verdict);
        Assert.Contains("!analyze -v", verdict);
    }

    [Fact]
    public void EvidenceLineIsAlwaysAppended()
    {
        string verdict = WilTriage.Classify("... code 80000003 (first chance)", WslWilStack);
        Assert.Contains("[code=80000003 1st-chance", verdict);
        Assert.Contains("top=", verdict);
    }
}
