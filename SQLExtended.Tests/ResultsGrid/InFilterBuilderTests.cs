using SQLExtended.ResultsGrid;
using Xunit;

namespace SQLExtended.Tests.ResultsGrid;

/// <summary>
/// Pins Copy as IN Filter's output. Every mistake it can make pastes and runs: an unquoted <c>007</c> matches 7, an
/// unescaped quote ends the literal early — so the text itself is what is asserted.
/// </summary>
public class InFilterBuilderTests
{
    [Fact]
    public void Guids_AreQuoted_OnePerLine_InOrder()
    {
        string text = InFilterBuilder.Build(new[]
        {
            "57B4D7DE-71BD-406E-A6F9-06DEB1560DD0",
            "E5E17456-8AE5-45BD-9BF3-10BEA3C654C8",
            "3075DBC1-7302-4711-BD91-1E9F833120AD"
        }, "uniqueidentifier");

        Assert.Equal(
            "IN (\r\n" +
            "    '57B4D7DE-71BD-406E-A6F9-06DEB1560DD0',\r\n" +
            "    'E5E17456-8AE5-45BD-9BF3-10BEA3C654C8',\r\n" +
            "    '3075DBC1-7302-4711-BD91-1E9F833120AD'\r\n" +
            ")", text);
    }

    [Fact]
    public void Numbers_AreNotQuoted()
    {
        Assert.Equal("IN (\r\n    10248,\r\n    10249,\r\n    10250\r\n)", InFilterBuilder.Build(new[] { "10248", "10249", "10250" }, "int"));
    }

    [Theory]
    [InlineData("decimal(18,2)")]
    [InlineData("money")]
    [InlineData("bit")]
    [InlineData("float")]
    [InlineData("varbinary(max)")]
    public void NumericAndBinaryTypes_AreNotQuoted(string type)
    {
        Assert.DoesNotContain("'", InFilterBuilder.Build(new[] { "1" }, type));
    }

    [Theory]
    [InlineData("varchar(10)")]
    [InlineData("date")]
    [InlineData("datetime2(7)")]
    public void TextAndDateTypes_AreQuoted_EvenWhenTheValuesAreDigits(string type)
    {
        Assert.Contains("'007'", InFilterBuilder.Build(new[] { "007" }, type));
    }

    [Fact]
    public void SingleQuotes_AreDoubled()
    {
        Assert.Contains("'O''Brien'", InFilterBuilder.Build(new[] { "O'Brien" }, "nvarchar(50)"));
    }

    [Fact]
    public void Nulls_AreLeftOut()
    {
        Assert.Equal("IN (\r\n    1,\r\n    3\r\n)", InFilterBuilder.Build(new[] { "1", null, "3" }, "int"));
    }

    [Fact]
    public void AllNull_GivesNull()
    {
        Assert.Null(InFilterBuilder.Build(new string[] { null, null }, "int"));
    }

    [Fact]
    public void Unicode_GetsTheNPrefix_OnlyWhenItNeedsIt()
    {
        string text = InFilterBuilder.Build(new[] { "Smith", "Zoë" }, "nvarchar(50)");
        Assert.Contains("'Smith'", text);
        Assert.Contains("N'Zoë'", text);
        Assert.DoesNotContain("N'Smith'", text);
    }

    [Fact]
    public void WithoutAType_NumbersAreUnquoted()
    {
        Assert.Equal("IN (\r\n    1,\r\n    -2.5\r\n)", InFilterBuilder.Build(new[] { "1", "-2.5" }, null));
    }

    [Theory]
    [InlineData("007")]
    [InlineData("abc")]
    [InlineData("2024-01-01")]
    [InlineData("")]
    public void WithoutAType_AnythingNotPlainlyANumber_IsQuoted(string value)
    {
        Assert.Contains("'" + value + "'", InFilterBuilder.Build(new[] { "1", value }, null));
    }

    [Fact]
    public void WithoutAType_ZeroPointSomething_IsANumber()
    {
        Assert.DoesNotContain("'", InFilterBuilder.Build(new[] { "0.5", "0" }, null));
    }
}
