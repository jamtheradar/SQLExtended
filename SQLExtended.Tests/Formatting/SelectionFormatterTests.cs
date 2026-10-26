using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLExtended.Formatting;
using Xunit;

namespace SQLExtended.Tests.Formatting;

/// <summary>
/// Pins Format Selection: it formats the statements the selection touches and leaves every other character of the
/// script where it was. The failures that matter are the quiet ones — a neighbouring statement reformatted, the body of a
/// procedure pulled to the left margin, a multi-line string padded — so most tests compare the text outside the span
/// and re-parse the result.
/// </summary>
public class SelectionFormatterTests
{
    private static readonly FormatterOptions Options = new() { IndentStyle = IndentStyleOption.Spaces, IndentSize = 4, KeywordCase = CasingOption.Upper };

    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static (SelectionFormatResult Result, string Text) Run(string doc, string selected, int occurrence = 0)
    {
        int start = -1;
        for (int i = 0; i <= occurrence; i++)
            start = doc.IndexOf(selected, start + 1, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{selected}' is not in the document");
        return Run(doc, start, start + selected.Length);
    }

    private static (SelectionFormatResult Result, string Text) Run(string doc, int start, int end)
    {
        var result = SelectionFormatter.Format(doc, start, end, Options);
        string text = result.Success ? doc.Substring(0, result.Start) + result.Replacement + doc.Substring(result.End) : doc;
        return (result, text);
    }

    private static void AssertParses(string sql)
    {
        new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void APartialSelection_FormatsTheWholeStatement_AndNothingElse()
    {
        string doc = Lines(
            "select a from t1 where x=1",
            "",
            "select b,c from t2 where y=2",
            "",
            "select d from t3");

        var (result, text) = Run(doc, "from t2 where");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.StartsWith(Lines("select a from t1 where x=1", "", ""), text);
        Assert.EndsWith(Lines("", "", "select d from t3"), text);
        Assert.Contains("SELECT", text);
        Assert.DoesNotContain("select b,c", text);
        AssertParses(text);
    }

    [Fact]
    public void TheCaret_FormatsTheStatementItIsIn()
    {
        string doc = Lines("select a from t1", "select b from t2");
        int caret = doc.IndexOf("t2");

        var (result, text) = Run(doc, caret, caret);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.StartsWith("select a from t1\r\n", text);
        Assert.DoesNotContain("select b", text);
    }

    [Fact]
    public void TheCaretAtTheEndOfAStatement_StillMeansIt()
    {
        string doc = Lines("select a from t1;", "select b from t2");
        int caret = doc.IndexOf(';') + 1;

        var (result, text) = Run(doc, caret, caret);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain("select a", text);
        Assert.EndsWith("select b from t2", text);
    }

    [Fact]
    public void AStatementInsideAProcedure_IsFormattedAlone_AtItsOwnIndent()
    {
        string doc = Lines(
            "create procedure dbo.p @id int as",
            "begin",
            "    set nocount on;",
            "    select a, b from dbo.t where id = @id and b > 0",
            "    return 0;",
            "end");

        var (result, text) = Run(doc, "from dbo.t");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.StartsWith(Lines("create procedure dbo.p @id int as", "begin", "    set nocount on;", "    SELECT"), text);
        Assert.EndsWith(Lines("    return 0;", "end"), text);

        // Every line the formatter produced sits at least at the statement's indent.
        string replaced = text.Substring(result.Start, result.Replacement.Length);
        foreach (string line in replaced.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0))
            Assert.StartsWith("    ", line);
        AssertParses(text);
    }

    [Fact]
    public void ASelectionAcrossSiblings_TakesBoth_AndWhatIsBetween()
    {
        string doc = Lines("select 1 as a", "-- between", "select 2 as b", "select 3 as c");

        var (result, text) = Run(doc, "1 as a\r\n-- between\r\nselect 2");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("-- between", text);
        Assert.EndsWith("select 3 as c", text);
        Assert.Equal(doc.IndexOf("select 3"), result.End + 2);
        AssertParses(text);
    }

    [Fact]
    public void ASelectionAcrossABlockEdge_IsLiftedToTheEnclosingStatement()
    {
        string doc = Lines(
            "if @x = 1",
            "    select a from t1",
            "else",
            "    select b from t2",
            "select c from t3");

        var (result, text) = Run(doc, "t1\r\nelse\r\n    select b");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.Start);
        Assert.Contains("ELSE", text);
        Assert.EndsWith("select c from t3", text);
        AssertParses(text);
    }

    [Fact]
    public void AMultiLineString_IsNotPadded()
    {
        string doc = Lines(
            "begin",
            "    select 'line one",
            "line two' as s from t",
            "end");

        var (result, text) = Run(doc, "as s from");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("\r\nline two'", text);
    }

    [Fact]
    public void WhitespaceOnly_IsNotAStatement()
    {
        string doc = Lines("select 1", "", "", "select 2");
        int blank = doc.IndexOf("\r\n\r\n") + 2;

        var (result, _) = Run(doc, blank, blank + 2);

        Assert.False(result.Success);
    }

    [Fact]
    public void ABrokenScriptElsewhere_StillFormatsTheSelectedLines()
    {
        string doc = Lines("select a, b from t where x=1", "", "select from where (");

        var (result, text) = Run(doc, "from t");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.StartsWith("SELECT", text);
        Assert.EndsWith("select from where (", text);
    }

    [Fact]
    public void TheSemicolon_IsNotDoubled()
    {
        var options = new FormatterOptions { TrailingSemicolon = SemicolonOption.Always };
        string doc = Lines("select a from t1;", "select b from t2;");
        int caret = doc.IndexOf("t1");

        var result = SelectionFormatter.Format(doc, caret, caret, options);
        string text = doc.Substring(0, result.Start) + result.Replacement + doc.Substring(result.End);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(";;", text);
    }

    [Fact]
    public void Reindent_PadsEveryLineButTheFirst_AndSkipsBlankOnes()
    {
        Assert.Equal("SELECT a\r\n\tFROM t\r\n\r\n\tWHERE x = 1", SelectionFormatter.Reindent("SELECT a\nFROM t\n\nWHERE x = 1\n", "\t", "\r\n"));
    }
}
