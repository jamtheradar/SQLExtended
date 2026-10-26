using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLExtended.Formatting;
using Xunit;

namespace SQLExtended.Tests.Formatting;

/// <summary>
/// Pins Format With…'s rules. Each one is a change to individual tokens, and the promise is that it changes those and
/// nothing else — not the layout, not a keyword inside a string, not a column that happens to share a keyword's name.
/// </summary>
public class PresetFormatterTests
{
    private static readonly FormatterOptions Options = new() { IndentSize = 4 };

    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static string Apply(string text, FormatPreset preset)
    {
        var result = PresetFormatter.Format(text, 0, 0, preset, Options);
        Assert.True(result.Success, result.ErrorMessage);
        return text.Substring(0, result.Start) + result.Replacement + text.Substring(result.End);
    }

    private static void AssertParses(string sql)
    {
        new TSql170Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void KeywordCase_KeepsTheLayout_AndLeavesStringsCommentsAndColumnsAlone()
    {
        string sql = Lines(
            "select   status, [date],",
            "      count(*) as n   -- select from where",
            "from dbo.t where name = 'select me'");

        string result = Apply(sql, new FormatPreset { KeywordCase = CasingOption.Upper });

        Assert.Equal(Lines(
            "SELECT   status, [date],",
            "      count(*) AS n   -- select from where",
            "FROM dbo.t WHERE name = 'select me'"), result);
    }

    [Fact]
    public void KeywordCase_Lower()
    {
        Assert.Equal("select a from t", Apply("SELECT a FROM t", new FormatPreset { KeywordCase = CasingOption.Lower }));
    }

    [Fact]
    public void FunctionCase_OnlyWhereCalled()
    {
        Assert.Equal("SELECT COUNT(*), count FROM t", Apply("SELECT count(*), count FROM t", new FormatPreset { FunctionCase = CasingOption.Upper }));
    }

    [Fact]
    public void RemoveBrackets_OnlyWhereTheyAreNotNeeded()
    {
        string result = Apply("SELECT [a], [two words], [select], [1x], [b] FROM [dbo].[t] WHERE c = '[d]'", new FormatPreset { RemoveBrackets = true });
        Assert.Equal("SELECT a, [two words], [select], [1x], b FROM dbo.t WHERE c = '[d]'", result);
        AssertParses(result);
    }

    [Fact]
    public void ShortJoinKeywords_DropsOuter_AndNothingElse()
    {
        string result = Apply("SELECT 1 FROM a LEFT OUTER JOIN b ON 1=1 full  outer join c ON 1=1 -- left outer join", new FormatPreset { ShortJoinKeywords = true });
        Assert.Equal("SELECT 1 FROM a LEFT JOIN b ON 1=1 full  join c ON 1=1 -- left outer join", result);
    }

    [Fact]
    public void AddSemicolons_OnEveryStatement_NotOnBlocks_AndNotTwice()
    {
        string sql = Lines(
            "IF @x = 1",
            "BEGIN",
            "    SELECT 1",
            "    SELECT 2;",
            "END",
            "SELECT 3 -- note");

        string result = Apply(sql, new FormatPreset { AddSemicolons = true });

        Assert.Equal(Lines(
            "IF @x = 1",
            "BEGIN",
            "    SELECT 1;",
            "    SELECT 2;",
            "END",
            "SELECT 3; -- note"), result);
        AssertParses(result);
    }

    [Fact]
    public void AddSemicolons_InAProcedure_PutsOneAtTheEndOfEachBodyStatement()
    {
        string sql = Lines("CREATE PROCEDURE dbo.p AS", "SET NOCOUNT ON", "SELECT 1");
        string result = Apply(sql, new FormatPreset { AddSemicolons = true });
        Assert.Equal(Lines("CREATE PROCEDURE dbo.p AS", "SET NOCOUNT ON;", "SELECT 1;"), result);
    }

    [Fact]
    public void AddSemicolons_OnTextThatDoesNotParse_IsSkippedWithANote()
    {
        var result = PresetFormatter.Format("SELECT FROM WHERE (", 0, 0, new FormatPreset { AddSemicolons = true }, Options);
        Assert.True(result.Success);
        Assert.Equal("SELECT FROM WHERE (", result.Replacement);
        Assert.Contains("does not parse", result.Note);
    }

    [Fact]
    public void Indentation_ToTabs_AndBack_LeavesMultiLineStringsAlone()
    {
        string sql = "BEGIN\n        SELECT 'a\n        b'\n      SELECT 2\nEND";

        string tabs = Apply(sql, new FormatPreset { Indentation = IndentConversion.Tabs });
        Assert.Equal("BEGIN\n\t\tSELECT 'a\n        b'\n\t  SELECT 2\nEND", tabs);

        Assert.Equal(sql, Apply(tabs, new FormatPreset { Indentation = IndentConversion.Spaces }));
    }

    [Fact]
    public void TrimTrailingWhitespace_ExceptInsideAString()
    {
        string result = Apply("SELECT 1   \r\nSELECT 'a   \r\nb'\t\r\n", new FormatPreset { TrimTrailingWhitespace = true });
        Assert.Equal("SELECT 1\r\nSELECT 'a   \r\nb'\r\n", result);
    }

    [Fact]
    public void WithASelection_OnlyItsLinesChange()
    {
        string sql = Lines("select 1", "select 2", "select 3");
        int start = sql.IndexOf("ect 2");

        var result = PresetFormatter.Format(sql, start, start + 2, new FormatPreset { KeywordCase = CasingOption.Upper }, Options);
        string text = sql.Substring(0, result.Start) + result.Replacement + sql.Substring(result.End);

        Assert.Equal(Lines("select 1", "SELECT 2", "select 3"), text);
    }

    [Fact]
    public void FullFormat_WithASelection_UsesFormatSelection()
    {
        string sql = Lines("select a from t1", "select b,c from t2 where y=2");
        int start = sql.IndexOf("from t2");

        var result = PresetFormatter.Format(sql, start, start + 4, new FormatPreset { FullFormat = true }, Options);
        string text = sql.Substring(0, result.Start) + result.Replacement + sql.Substring(result.End);

        Assert.StartsWith("select a from t1\r\nSELECT", text);
        AssertParses(text);
    }

    [Fact]
    public void NoRules_IsAFailure_NotASilentNoOp()
    {
        Assert.False(PresetFormatter.Format("select 1", 0, 0, new FormatPreset(), Options).Success);
    }

    [Fact]
    public void TheDefaults_AreTheTwoTheIssueNames()
    {
        List<FormatPreset> defaults = FormatPreset.Defaults();
        Assert.Equal(new[] { "Casing only", "Full format" }, defaults.ConvertAll(p => p.Name));
        Assert.False(defaults[0].FullFormat);
        Assert.True(defaults[1].FullFormat);
    }
}
