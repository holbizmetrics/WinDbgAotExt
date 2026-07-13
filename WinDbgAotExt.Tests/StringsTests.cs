using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinDbgAotExt.Bridge;
using Xunit;

namespace WinDbgAotExt.Tests;

// The two PURE seams of !strings: regex compilation (bad patterns must surface, not throw deep in the
// heap walk) and the listing formatter (incl. the dropped-tail report — never silently truncate). The
// ClrMD heap walk itself needs a managed target and is proven in cdb.
public class StringsTests
{
    [Fact]
    public void TryCompilePattern_EmptyOrWhitespace_IsMatchAll()
    {
        Assert.True(StringRendering.TryCompilePattern("", out Regex? empty, out _));
        Assert.Null(empty);   // null regex == match all
        Assert.True(StringRendering.TryCompilePattern("   ", out Regex? blank, out _));
        Assert.Null(blank);
    }

    [Fact]
    public void TryCompilePattern_ValidRegex_Compiles()
    {
        Assert.True(StringRendering.TryCompilePattern(@"widget-\d+", out Regex? regex, out string? error));
        Assert.NotNull(regex);
        Assert.Null(error);
        Assert.Matches(regex!, "widget-42");
    }

    [Fact]
    public void TryCompilePattern_InvalidRegex_ReportsInsteadOfThrowing()
    {
        Assert.False(StringRendering.TryCompilePattern("(unclosed", out Regex? regex, out string? error));
        Assert.Null(regex);
        Assert.NotNull(error);
        Assert.Contains("invalid regex", error!);
    }

    [Fact]
    public void Format_UnderCap_ListsAllWithNoTailNote()
    {
        var hits = new List<StringHit>
        {
            new StringHit { Address = 0x1000, Value = "connection=Server=db;" },
            new StringHit { Address = 0x2000, Value = "https://example.test/api" },
        };
        string listing = StringRendering.Format(hits, totalMatched: 2, cap: 200, pattern: null);

        Assert.Contains("2 managed string(s)", listing);
        Assert.Contains("0x1000", listing);
        Assert.Contains("\"connection=Server=db;\"", listing);
        Assert.DoesNotContain("more", listing);   // nothing dropped
    }

    [Fact]
    public void Format_OverCap_ReportsTheDroppedTail()
    {
        var shown = new List<StringHit>
        {
            new StringHit { Address = 0x1000, Value = "a" },
            new StringHit { Address = 0x2000, Value = "b" },
        };
        // 500 matched, only 2 shown (cap 2): the operator must be told 498 are hidden.
        string listing = StringRendering.Format(shown, totalMatched: 500, cap: 2, pattern: "x");

        Assert.Contains("500 managed string(s) matching /x/", listing);
        Assert.Contains("showing first 2", listing);
        Assert.Contains("498 more", listing);
        Assert.Contains("--all", listing);   // tells them how to see the rest
    }

    [Fact]
    public void Format_NoMatches_SaysSo_WithPatternScope()
    {
        string listing = StringRendering.Format(new List<StringHit>(), totalMatched: 0, cap: 200, pattern: "nope");
        Assert.Contains("no managed strings matching /nope/ found", listing);
    }
}
