using System.Linq;
using SQLExtended.ResultsGrid.Aggregates;
using Xunit;

namespace SQLExtended.Tests.ResultsGrid;

/// <summary>
/// The UI-to-storage column mapping behind every multi-column grid selection (issue #16). A wrong answer is never an
/// error: the aggregates pane totals a column that was not selected, or an export carries the wrong one.
/// </summary>
public class GridColumnRunsTests
{
    /// <summary>A grid whose on-screen order is <paramref name="storageByUi"/>: index = UI column, value = storage column.</summary>
    private static (int, int)[] Runs(int uiStart, int uiEnd, params int[] storageByUi) =>
        GridColumnRuns.FromUiSpan(uiStart, uiEnd, ui => storageByUi[ui]).ToArray();

    [Fact]
    public void AnUnreorderedGridIsOneRun()
    {
        Assert.Equal(new[] { (2, 5) }, Runs(2, 5, 0, 1, 2, 3, 4, 5));
    }

    [Fact]
    public void AColumnDraggedOutOfTheSpanIsNotRead()
    {
        // Storage column 4 was dragged to the far right, so UI 1..4 show storage 1, 2, 3, 5. Mapping only the ends read
        // storage 1..5, pulling in column 4, which is not in the selection.
        Assert.Equal(new[] { (1, 3), (5, 5) }, Runs(1, 4, 0, 1, 2, 3, 5, 4));
    }

    [Fact]
    public void ReversedEndsNoLongerDropTheBlock()
    {
        // Columns swapped: on screen storage 3 sits before storage 2. End-only mapping gave ColStart 3 > ColEnd 2, and the
        // block was skipped without a word.
        Assert.Equal(new[] { (3, 3), (2, 2) }, Runs(2, 3, 0, 1, 3, 2));
    }

    [Fact]
    public void RunsAreInOnScreenOrder()
    {
        Assert.Equal(new[] { (4, 5), (1, 2) }, Runs(1, 4, 0, 4, 5, 1, 2));
    }

    [Fact]
    public void ASingleColumnIsOneRun()
    {
        Assert.Equal(new[] { (3, 3) }, Runs(2, 2, 0, 1, 3, 2));
    }

    [Fact]
    public void AnEmptySpanHasNoRuns()
    {
        Assert.Empty(Runs(3, 2, 0, 1, 2, 3));
    }
}
