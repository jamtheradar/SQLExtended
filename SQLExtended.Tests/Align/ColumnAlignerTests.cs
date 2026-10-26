using SQLExtended.Align;
using Xunit;

namespace SQLExtended.Tests.Align;

/// <summary>
/// Pins Align On. Its failures are all of one kind — padding in front of something that only looks like the token (the
/// AS in a CAST, an = in a string, the ON in a comment) — and the result still runs, so only the text shows it.
/// </summary>
public class ColumnAlignerTests
{
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static string S(int n) => new(' ', n);

    [Fact]
    public void TheIssueExample_AlignedOnAs_ThenOn_ThenEquals()
    {
        string sql = Lines(
            "SELECT o.OrderId AS Id,",
            "       o.CustomerId AS Customer,",
            "       o.OrderDate AS Placed",
            "FROM dbo.Orders o",
            "JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "JOIN dbo.Regions r ON r.RegionId = c.RegionId");

        sql = ColumnAligner.Align(sql, "AS").Text;
        sql = ColumnAligner.Align(sql, "ON").Text;
        sql = ColumnAligner.Align(sql, "=").Text;

        Assert.Equal(Lines(
            "SELECT o.OrderId    AS Id,",
            "       o.CustomerId AS Customer,",
            "       o.OrderDate  AS Placed",
            "FROM dbo.Orders o",
            "JOIN dbo.Customers c ON c.CustomerId = o.CustomerId",
            "JOIN dbo.Regions r   ON r.RegionId   = c.RegionId"), sql);
    }

    [Fact]
    public void AligningTwice_ChangesNothing_AndTooFarOutIsPulledIn()
    {
        string once = ColumnAligner.Align(Lines("a = 1", "bbb = 2"), "=").Text;
        Assert.Equal(Lines("a   = 1", "bbb = 2"), once);
        Assert.False(ColumnAligner.Align(once, "=").Changed);
        Assert.Equal(once, ColumnAligner.Align(Lines("a        = 1", "bbb      = 2"), "=").Text);
    }

    [Fact]
    public void WordTokens_MatchWholeWordsOnly_AnyCase()
    {
        string result = ColumnAligner.Align(Lines("SELECT ascii as a,", "  basis As bb"), "AS").Text;
        Assert.Equal(Lines("SELECT ascii as a,", "  basis" + S(6) + "As bb"), result);
    }

    [Fact]
    public void ParenthesesOpenedOnTheLine_AreSkipped()
    {
        string result = ColumnAligner.Align(Lines("SELECT CAST(x AS int) AS a,", "       y AS bb"), "AS").Text;
        Assert.Equal(Lines("SELECT CAST(x AS int) AS a,", "       y" + S(14) + "AS bb"), result);
    }

    [Fact]
    public void StringsCommentsAndBrackets_AreSkipped()
    {
        string result = ColumnAligner.Align(Lines(
            "x = 'a = b' -- c = d",
            "[p = q] = 2",
            "/* r = s */ yy = 3"), "=").Text;

        Assert.Equal(Lines(
            "x" + S(14) + "= 'a = b' -- c = d",
            "[p = q]" + S(8) + "= 2",
            "/* r = s */ yy = 3"), result);
    }

    [Fact]
    public void BlockCommentsAndStringsSpanningLines_AreSkippedThroughout()
    {
        string result = ColumnAligner.Align(Lines(
            "a = 1 /* starts",
            "  b = 2 still comment */",
            "SET @s = 'opens",
            "c = 3' + x",
            "dd = 4"), "=").Text;

        Assert.Equal(Lines(
            "a" + S(6) + "= 1 /* starts",
            "  b = 2 still comment */",
            "SET @s = 'opens",
            "c = 3' + x",
            "dd" + S(5) + "= 4"), result);
    }

    [Fact]
    public void Operators_DoNotMatchInsideLongerOnes()
    {
        string result = ColumnAligner.Align(Lines("a >= 1", "SET @n += 1", "bb = 2", "c = 3"), "=").Text;
        Assert.Equal(Lines("a >= 1", "SET @n += 1", "bb = 2", "c  = 3"), result);
    }

    [Fact]
    public void LineComments_AlignOnDoubleDash_AndACommentOnlyLineStays()
    {
        string result = ColumnAligner.Align(Lines("-- header stays", "a = 1, -- one", "bbbb = 2, -- two"), "--").Text;
        Assert.Equal(Lines("-- header stays", "a = 1," + S(4) + "-- one", "bbbb = 2, -- two"), result);
    }

    [Fact]
    public void ALeadingToken_IsPassedOver()
    {
        string sql = Lines("SELECT a", "     , bb", "     , c");
        Assert.Equal(sql, ColumnAligner.Align(sql, ",").Text);
    }

    [Fact]
    public void ATightToken_StaysTightOnTheLongestLine()
    {
        string result = ColumnAligner.Align(Lines("a, b, c", "dddd, e, f"), ",").Text;
        Assert.Equal(Lines("a   , b, c", "dddd, e, f"), result);
    }

    [Fact]
    public void EveryOccurrence_AlignsEachInTurn_InsideParenthesesToo()
    {
        string result = ColumnAligner.Align(Lines("(1, 'a', 10),", "(200, 'bcd', 3),"), ",", everyOccurrence: true).Text;
        Assert.Equal(Lines("(1  , 'a'  , 10),", "(200, 'bcd', 3) ,"), result);
    }

    [Fact]
    public void Tabs_PadToATabStop()
    {
        string result = ColumnAligner.Align("a = 1\nbbbbb = 2", "=", useTabs: true, tabSize: 4).Text;
        Assert.Equal("a\t\t= 1\nbbbbb\t= 2", result);
    }

    [Fact]
    public void TabIndentedLines_AreMeasuredByTheirVisualWidth()
    {
        string result = ColumnAligner.Align("\ta = 1\n        bb = 2", "=", tabSize: 4).Text;
        Assert.Equal("\ta" + S(6) + "= 1\n        bb = 2", result);
    }

    [Fact]
    public void FewerThanTwoLinesWithTheToken_IsNoChange()
    {
        var result = ColumnAligner.Align(Lines("a = 1", "b"), "=");
        Assert.False(result.Changed);
        Assert.Equal(0, result.LinesAligned);
    }

    [Fact]
    public void LineEndings_AreKept()
    {
        Assert.Equal("a  = 1\nbb = 2\r\n", ColumnAligner.Align("a = 1\nbb = 2\r\n", "=").Text);
    }
}
