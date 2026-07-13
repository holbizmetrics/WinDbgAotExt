using System.Collections.Generic;
using WinDbgAotExt.Bridge;
using Xunit;

namespace WinDbgAotExt.Tests;

// The two PURE seams of !fields: address parsing (WinDbg hands addresses in three notations) and the
// listing formatter (what the operator actually reads, incl. the drill-in hint). The ClrMD read itself
// needs a live managed target and is proven in cdb; these lock the parts that don't.
public class FieldsTests
{
    [Theory]
    [InlineData("0x1c4a3b20010", 0x1c4a3b20010UL)]   // 0x-prefixed
    [InlineData("1c4a3b20010", 0x1c4a3b20010UL)]     // bare hex
    [InlineData("0000001c`4a3b0010", 0x1c4a3b0010UL)] // WinDbg backtick-grouped
    [InlineData("  0x2000  ", 0x2000UL)]             // whitespace tolerated
    public void TryParseAddress_AcceptsTheThreeNotations(string text, ulong expected)
    {
        Assert.True(FieldRendering.TryParseAddress(text, out ulong address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("0xZZZ")]
    public void TryParseAddress_RejectsNonHex(string text)
    {
        Assert.False(FieldRendering.TryParseAddress(text, out _));
    }

    [Fact]
    public void FormatFields_RendersValuesAndDrillInHintForFirstObjectRef()
    {
        var fields = new List<FieldInfo>
        {
            new FieldInfo { Name = "Id",    TypeName = "System.Int32",  Value = "42" },
            new FieldInfo { Name = "Label", TypeName = "System.String", Value = "\"widget-42\"" },
            new FieldInfo { Name = "Next",  TypeName = "Widget",        Value = "Widget @ 0x1c4a3b20500", ObjectAddress = 0x1c4a3b20500 },
        };

        string listing = FieldRendering.FormatFields(0x1c4a3b20010, fields);

        Assert.Contains("System.Int32 Id = 42", listing);
        Assert.Contains("System.String Label = \"widget-42\"", listing);
        Assert.Contains("Widget Next = Widget @ 0x1c4a3b20500", listing);
        // the drill-in hint points at the first object reference
        Assert.Contains("(drill in: !fields 0x1c4a3b20500)", listing);
    }

    [Fact]
    public void FormatFields_NoObjectRefs_NoDrillInHint()
    {
        var fields = new List<FieldInfo>
        {
            new FieldInfo { Name = "X", TypeName = "System.Int32", Value = "1" },
            new FieldInfo { Name = "Y", TypeName = "System.Int32", Value = "2" },
        };
        string listing = FieldRendering.FormatFields(0x2000, fields);
        Assert.DoesNotContain("drill in", listing);
    }

    [Fact]
    public void FormatFields_EmptyList_SaysNoReadableFields()
    {
        string listing = FieldRendering.FormatFields(0xDEAD, new List<FieldInfo>());
        Assert.Contains("no readable instance fields", listing);
        Assert.Contains("dead", listing);   // the address is echoed in hex
    }
}
