using System;
using System.Collections.Generic;

namespace SQLExtended.ResultsGrid.Aggregates;

/// <summary>
/// Turns a span of on-screen (UI) columns into the storage columns it shows, as runs of consecutive storage indexes.
///
/// <para>Once a column header has been dragged, the UI columns between a block's <c>X</c> and <c>Right</c> are not the
/// storage columns between the two ends' storage indexes. Mapping only the ends reads columns that are not selected,
/// misses ones that are, and drops the block entirely when the mapped ends come out reversed (issue #16). Each UI
/// column is mapped on its own instead, and the result is grouped back into runs so a grid nobody has reordered still
/// comes out as the single range it always was.</para>
///
/// <para>Free of the grid assembly so the test project links it (<c>SQLExtended.Tests/ResultsGrid/GridColumnRunsTests.cs</c>).</para>
/// </summary>
internal static class GridColumnRuns
{
    /// <summary>
    /// Storage runs for UI columns <paramref name="uiStart"/>..<paramref name="uiEnd"/> (inclusive), in on-screen order.
    /// A run grows only while the next UI column's storage index is the previous one plus one.
    /// </summary>
    public static List<(int Start, int End)> FromUiSpan(int uiStart, int uiEnd, Func<int, int> toStorage)
    {
        var runs = new List<(int Start, int End)>();
        if (uiEnd < uiStart) return runs;

        int start = toStorage(uiStart), end = start;
        for (int ui = uiStart + 1; ui <= uiEnd; ui++)
        {
            int storage = toStorage(ui);
            if (storage == end + 1)
            {
                end = storage;
                continue;
            }
            runs.Add((start, end));
            start = end = storage;
        }
        runs.Add((start, end));
        return runs;
    }
}
