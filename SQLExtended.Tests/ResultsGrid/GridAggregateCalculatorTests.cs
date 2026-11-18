using System;
using System.Globalization;
using System.Threading;
using SQLExtended.ResultsGrid.Aggregates;
using Xunit;

namespace SQLExtended.Tests.ResultsGrid;

/// <summary>
/// The aggregate arithmetic. Every failure mode here is a number that looks right — nothing throws, no
/// grid goes blank — so the assertions are about exactness and about which values are counted, not about
/// the code running.
///
/// <para>Parsing and formatting follow the current culture, as the grid and the window do, so every test runs under
/// en-US unless it sets a culture of its own. Without that the expected "3.00" is "3,00" on a pt-BR or de-DE machine,
/// and "1,5" is a number there rather than text (issue #17). xUnit builds a new instance per test and runs the
/// constructor, the test and <see cref="Dispose"/> on the same thread.</para>
/// </summary>
public class GridAggregateCalculatorTests : IDisposable
{
    private readonly CultureInfo _previousCulture = Thread.CurrentThread.CurrentCulture;

    public GridAggregateCalculatorTests() => Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => Thread.CurrentThread.CurrentCulture = _previousCulture;

    private static GridColumnAggregate Aggregate(params string[] values)
    {
        var accumulator = new GridAggregateAccumulator();
        foreach (string value in values)
            accumulator.Add(value);
        return accumulator.Build("col", 0);
    }

    // --- Counting ---------------------------------------------------------------------------------

    [Fact]
    public void NullsBlanksAndValuesAreCountedSeparately()
    {
        var result = Aggregate("1", null, "", "   ", "2");

        Assert.Equal(5, result.Cells);
        Assert.Equal(4, result.NonNull);   // SQL COUNT(col): the NULL is excluded, the blanks are not
        Assert.Equal(1, result.Nulls);
        Assert.Equal(2, result.Blanks);
    }

    [Fact]
    public void DistinctFollowsSqlSemantics()
    {
        // COUNT(DISTINCT col) ignores NULL but treats the empty string as a value.
        var result = Aggregate("a", "a", "b", null, null, "");

        Assert.Equal(3, result.DistinctCount);
    }

    [Fact]
    public void DistinctIsCaseSensitive()
    {
        // The grid shows the two renderings differently, so counting them as one would contradict what is
        // on screen — regardless of the column's collation, which we cannot see from here.
        Assert.Equal(2, Aggregate("Foo", "foo").DistinctCount);
    }

    // --- Type inference ---------------------------------------------------------------------------

    [Fact]
    public void AColumnOfNumbersIsNumeric()
    {
        var result = Aggregate("1", "2.5", "-3");

        Assert.Equal(GridValueKind.Numeric, result.Kind);
        Assert.Equal(0.5m, result.SumDecimal);
    }

    [Fact]
    public void OneNonNumericValueMakesTheWholeColumnText()
    {
        // The important half: no Sum is offered rather than a Sum that quietly skipped a row.
        var result = Aggregate("1", "2", "n/a");

        Assert.Equal(GridValueKind.Text, result.Kind);
        Assert.Null(result.SumDecimal);
        Assert.Null(result.SumDouble);
        Assert.Null(GridAggregateFormat.Sum(result));
    }

    [Fact]
    public void NullsAndBlanksDoNotDefeatNumericInference()
    {
        var result = Aggregate("10", null, "  ", "20");

        Assert.Equal(GridValueKind.Numeric, result.Kind);
        Assert.Equal(30m, result.SumDecimal);
    }

    [Fact]
    public void AColumnOfOnlyNullsHasNoType()
    {
        var result = Aggregate(null, null);

        Assert.Equal(GridValueKind.Empty, result.Kind);
        Assert.Null(result.MinText);
        Assert.Null(GridAggregateFormat.Sum(result));
    }

    [Fact]
    public void DatesAreRecognisedAndNotSummed()
    {
        var result = Aggregate("2026-08-12 14:03:21.000", "2024-01-02 00:00:00.000");

        Assert.Equal(GridValueKind.DateTime, result.Kind);
        Assert.Null(result.SumDecimal);
        Assert.Equal("2024-01-02 00:00:00.000", result.MinText);
        Assert.Equal("2026-08-12 14:03:21.000", result.MaxText);
    }

    // --- Exactness --------------------------------------------------------------------------------

    [Fact]
    public void MoneyIsSummedExactly()
    {
        // The whole reason the accumulator prefers decimal: in double this totals 0.30000000000000004.
        var result = Aggregate("0.10", "0.20");

        Assert.Equal(0.30m, result.SumDecimal);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void ASumKeepsTheDecimalPlacesItWasGiven()
    {
        // A money column totalling to a round number must still show cents, or it reads as an integer column.
        var result = Aggregate("1.50", "1.50");

        Assert.Equal(2, result.Scale);
        Assert.Equal("3.00", GridAggregateFormat.Sum(result));
    }

    [Fact]
    public void SumsAndAveragesAreShownInTheCurrentCulture()
    {
        // The window is read by the user, so a pt-BR user sees a comma decimal separator, as in their other tools.
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");

        Assert.Equal("3,00", GridAggregateFormat.Sum(Aggregate("1,50", "1,50")));
        Assert.Equal("1,67", GridAggregateFormat.Average(Aggregate("1", "2", "2")));
    }

    [Fact]
    public void AValueBeyondDecimalFallsBackToDoubleAndSaysSo()
    {
        var result = Aggregate("1E+300", "1");

        Assert.Equal(GridValueKind.Numeric, result.Kind);
        Assert.Null(result.SumDecimal);
        Assert.True(result.Approximate);
        Assert.NotNull(result.SumDouble);
    }

    [Fact]
    public void AverageShowsDecimalsEvenForWholeNumbers()
    {
        // Averaging 1, 2, 2 is 1.67 — reporting "2" would be the wrong answer rendered confidently.
        var result = Aggregate("1", "2", "2");

        Assert.Equal("1.67", GridAggregateFormat.Average(result));
    }

    // --- Extremes ---------------------------------------------------------------------------------

    [Fact]
    public void NumericMinMaxCompareNumericallyNotAsText()
    {
        // As text "9" > "10", which is the classic way a Max column lies.
        var result = Aggregate("9", "10", "100");

        Assert.Equal("9", result.MinText);
        Assert.Equal("100", result.MaxText);
    }

    [Fact]
    public void MinMaxReportTheCellTextVerbatim()
    {
        // Never reformatted: re-rendering would imply a precision the grid did not show.
        var result = Aggregate("00012.500", "9.1");

        Assert.Equal("9.1", result.MinText);
        Assert.Equal("00012.500", result.MaxText);
    }

    [Fact]
    public void TextMinMaxAreOrdinal()
    {
        var result = Aggregate("pear", "apple", "fig");

        Assert.Equal(GridValueKind.Text, result.Kind);
        Assert.Equal("apple", result.MinText);
        Assert.Equal("pear", result.MaxText);
    }

    // --- Text length ------------------------------------------------------------------------------

    [Fact]
    public void CharCountsCoverNonNullCellsOnly()
    {
        var result = Aggregate("abc", null, "de");

        Assert.Equal(5, result.TotalChars);
        Assert.Equal(3, result.MaxChars);
    }

    // --- Parsing ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("1,5")]      // a German-rendered decimal, which DateTime.TryParse accepts under en-US (the class's culture)
    [InlineData("May")]
    [InlineData("Q1")]
    public void ShortTextIsNotMistakenForADate(string value)
    {
        // DateTime.TryParse on its own is permissive enough to make a text column report as Date/time and
        // order its Min/Max chronologically. Every date SQL Server renders carries a '-' or a ':'.
        Assert.Equal(GridValueKind.Text, Aggregate(value).Kind);
    }

    [Theory]
    [InlineData("2026-08-12")]
    [InlineData("2026-08-12 14:03:21.000")]
    [InlineData("14:03:21.0000000")]
    [InlineData("2026-08-12 14:03:21.0000000 +10:00")]
    public void TheDateShapesSqlServerRendersStillParse(string value)
    {
        Assert.Equal(GridValueKind.DateTime, Aggregate(value).Kind);
    }

    [Fact]
    public void GuidsAndBinaryAreNotMistakenForNumbers()
    {
        Assert.Equal(GridValueKind.Text, Aggregate("0x0A1B2C").Kind);
        Assert.Equal(GridValueKind.Text, Aggregate("6F9619FF-8B86-D011-B42D-00C04FC964FF").Kind);
    }

    [Fact]
    public void ACommaIsNotReadAsAThousandsSeparator()
    {
        // AllowThousands is deliberately off: under an English culture .NET would otherwise parse the
        // German rendering of 1.5 as 15, silently inflating the total tenfold. The grid never renders
        // group separators, so refusing them costs nothing.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var result = Aggregate("1,5");

            Assert.Equal(GridValueKind.Text, result.Kind);
            Assert.Null(result.SumDecimal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NumbersRenderedInTheCurrentCultureParse()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var result = Aggregate("1,5", "2,5");

            Assert.Equal(GridValueKind.Numeric, result.Kind);
            Assert.Equal(4.0m, result.SumDecimal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
