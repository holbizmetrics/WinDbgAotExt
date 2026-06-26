using WinDbgAotExt;
using Xunit;

namespace WinDbgAotExt.Tests;

// Unit tests for CommandHost.Argv -- the command-line parser a !command receives.
// Pure logic (quote toggling, \" and \\ escapes, whitespace splitting); no debugger,
// no DbgEng, no native interop. Runs under plain `dotnet test`.
public class ArgvTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \t")]
    [InlineData(null)]
    public void EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        Assert.Empty(CommandHost.Argv(input!));
    }

    [Fact]
    public void SingleWord()
    {
        Assert.Equal(new[] { "abc" }, CommandHost.Argv("abc"));
    }

    [Fact]
    public void MultipleWords_CollapseRunsOfWhitespace()
    {
        Assert.Equal(new[] { "a", "b", "c" }, CommandHost.Argv("a  b\tc"));
    }

    [Fact]
    public void QuotedString_PreservesInteriorSpaces()
    {
        Assert.Equal(new[] { "hello world" }, CommandHost.Argv("\"hello world\""));
    }

    [Fact]
    public void QuotedSegmentThenBareWord()
    {
        Assert.Equal(new[] { "a b", "c" }, CommandHost.Argv("\"a b\" c"));
    }

    [Fact]
    public void EscapedQuote_BecomesLiteralQuote_NotAToggle()
    {
        // input chars: a \ " b   ->  the \" is a literal '"', so one token a"b
        Assert.Equal(new[] { "a\"b" }, CommandHost.Argv("a\\\"b"));
    }

    [Fact]
    public void EscapedBackslash_BecomesSingleBackslash()
    {
        // input chars: a \ \ b   ->  one token a\b
        Assert.Equal(new[] { "a\\b" }, CommandHost.Argv("a\\\\b"));
    }

    [Fact]
    public void UnterminatedQuote_KeepsRemainderAsOneToken()
    {
        Assert.Equal(new[] { "a b" }, CommandHost.Argv("\"a b"));
    }
}
