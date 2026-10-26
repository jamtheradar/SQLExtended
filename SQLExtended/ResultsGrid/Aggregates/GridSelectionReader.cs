using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Reflection;
using Microsoft.SqlServer.Management.UI.Grid;

namespace SQLExtended.ResultsGrid.Aggregates;

/// <summary>Why a read produced nothing, so the pane can say which of the three it was.</summary>
internal enum GridSelectionStatus
{
    Ok,
    /// <summary>No grid, or nothing selected in it.</summary>
    NoSelection,
    /// <summary>The selection is larger than the configured cell cap. Nothing is computed — see the
    /// class remarks on <see cref="GridSelectionReader"/>.</summary>
    TooLarge
}

internal sealed class GridSelectionRead
{
    public GridSelectionStatus Status { get; set; }
    public GridAggregateResult Result { get; set; }

    /// <summary>Cells the selection covers, counted before the cap is applied — so a "too large"
    /// message can name the real size.</summary>
    public long SelectedCells { get; set; }

    public int SelectedColumns { get; set; }
}

/// <summary>
/// Turns a results grid's current selection into aggregates.
///
/// <para><b>Must run on the UI thread</b> — <see cref="GridControl"/> is a WinForms control and its
/// storage reads are not thread-safe. The work is bounded instead of moved: see the cap below.</para>
///
/// <para><b>An over-cap selection computes nothing rather than a prefix.</b> Reading N cells is N calls
/// into <c>IGridStorage</c> on the UI thread, so an unbounded Ctrl+A on a million-row grid would freeze
/// SSMS. The tempting fix — total the first N cells and note the truncation — produces a plausible,
/// wrong number in the one place a wrong number is invisible: a totals panel. So the cap refuses, names
/// the actual size, and points at the setting that raises it.</para>
/// </summary>
internal static class GridSelectionReader
{
    /// <summary>
    /// Reads the grid's selection and aggregates it. Never throws; a failure reads as
    /// <see cref="GridSelectionStatus.NoSelection"/>.
    /// </summary>
    public static GridSelectionRead Read(GridControl grid, long maxCells)
    {
        var read = new GridSelectionRead { Status = GridSelectionStatus.NoSelection };
        if (grid == null)
            return read;

        try
        {
            IGridStorage storage = grid.GridStorage;
            if (storage == null)
                return read;

            BlockOfCellsCollection blocks = grid.SelectedCells;
            if (blocks == null || blocks.Count == 0)
                return read;

            long numRows = storage.NumRows();
            int numCols = grid.ColumnsNumber;
            if (numRows <= 0 || numCols <= 0)
                return read;

            var ranges = ResolveRanges(grid, blocks, numRows, numCols);
            if (ranges.Count == 0)
                return read;

            // Size the job before doing any of it. Overlapping blocks make this an upper bound, which is
            // the right direction to be wrong in for a cap.
            long cells = 0;
            foreach (var range in ranges)
                cells += (range.RowEnd - range.RowStart + 1) * (range.ColEnd - range.ColStart + 1);

            read.SelectedCells = cells;
            if (cells > maxCells)
            {
                read.Status = GridSelectionStatus.TooLarge;
                return read;
            }

            read.Result = Aggregate(grid, storage, ranges, out int columnCount);
            read.SelectedColumns = columnCount;
            read.Status = read.Result.Columns.Count > 0 ? GridSelectionStatus.Ok : GridSelectionStatus.NoSelection;
            return read;
        }
        catch
        {
            // A grid disposed mid-read (the query window closed while we were debounced) is the common
            // case and is not worth reporting as an error.
            return new GridSelectionRead { Status = GridSelectionStatus.NoSelection };
        }
    }

    internal struct CellRange
    {
        public long RowStart, RowEnd;
        public int ColStart, ColEnd;
    }

    /// <summary>
    /// Expands each selection block into a concrete row/column range.
    ///
    /// <para>Two things about <see cref="GridControl.SelectedCells"/> make this more than reading X/Y/
    /// Right/Bottom, and both were established by decompiling the control:</para>
    /// <list type="bullet">
    /// <item><b>Which bounds are meaningful depends on <see cref="GridControl.SelectionType"/>.</b> For a
    /// whole-column selection the block's Y/Bottom describe only where the click landed, not the extent —
    /// the real range is every row. For a whole-row selection it is X/Right that are meaningless. Reading
    /// all four unconditionally totals one cell of a column the user selected entirely, which looks like a
    /// working feature. The control's own clipboard path (<c>GetClipboardTextForSelectionBlock</c>)
    /// switches on exactly this.</item>
    /// <item><b>The column indexes are UI indexes for four of the six selection types and storage indexes
    /// for the other two.</b> <c>AdjustColumnIndexesInSelectedCells</c> returns the collection untouched
    /// for CellBlocks/ColumnBlocks/RowBlocks/SingleRow and remaps only SingleCell/SingleColumn. Getting
    /// this backwards reads a neighbouring column whenever columns have been reordered.</item>
    /// </list>
    /// <para>Bounds are inclusive: <c>Width == Right - X + 1</c>.</para>
    /// </summary>
    internal static List<CellRange> ResolveRanges(GridControl grid, BlockOfCellsCollection blocks, long numRows, int numCols)
    {
        GridSelectionType selectionType = grid.SelectionType;
        bool wholeColumns = selectionType == GridSelectionType.ColumnBlocks || selectionType == GridSelectionType.SingleColumn;
        bool wholeRows = selectionType == GridSelectionType.RowBlocks || selectionType == GridSelectionType.SingleRow;
        bool storageIndexed = selectionType == GridSelectionType.SingleCell || selectionType == GridSelectionType.SingleColumn;

        var ranges = new List<CellRange>();
        foreach (BlockOfCells block in blocks)
        {
            if (block == null || block.IsEmpty)
                continue;

            var range = new CellRange();

            if (wholeColumns)
            {
                range.RowStart = 0;
                range.RowEnd = numRows - 1;
                range.ColStart = block.X;
                range.ColEnd = block.Right;
            }
            else if (wholeRows)
            {
                range.RowStart = block.Y;
                range.RowEnd = block.Bottom;
                range.ColStart = 0;
                range.ColEnd = numCols - 1;
            }
            else
            {
                range.RowStart = block.Y;
                range.RowEnd = block.Bottom;
                range.ColStart = block.X;
                range.ColEnd = block.Right;
            }

            if (!storageIndexed)
            {
                range.ColStart = ToStorageColumn(grid, range.ColStart);
                range.ColEnd = ToStorageColumn(grid, range.ColEnd);
            }

            range.RowStart = Math.Max(0, range.RowStart);
            range.RowEnd = Math.Min(numRows - 1, range.RowEnd);
            if (range.RowEnd < range.RowStart || range.ColEnd < range.ColStart)
                continue;

            ranges.Add(range);
        }
        return ranges;
    }

    private static int ToStorageColumn(GridControl grid, int uiColumn)
    {
        try { return grid.GetStorageColumnIndexByUIIndex(uiColumn); }
        catch { return uiColumn; }
    }

    private static GridAggregateResult Aggregate(GridControl grid, IGridStorage storage, List<CellRange> ranges, out int columnCount)
    {
        // QEResultSet's internals, read the same way ResultsGridReader reads them — best effort, with the
        // header text as the fallback for names and the grid's "NULL" rendering as the fallback for nulls.
        StringCollection columnNames = null;
        MethodInfo isCellNull = null;
        Type storageType = storage.GetType();
        try { columnNames = storageType.GetProperty("ColumnNames", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(storage) as StringCollection; } catch { }
        try { isCellNull = storageType.GetMethod("IsCellDataNull", new[] { typeof(long), typeof(int) }); } catch { }

        int dataColumnCount = columnNames?.Count ?? Math.Max(0, grid.ColumnsNumber - 1);

        var accumulators = new Dictionary<int, GridAggregateAccumulator>();
        var order = new List<int>();
        var combined = new GridAggregateAccumulator();

        foreach (var range in ranges)
        {
            try { storage.EnsureRowsInBuf(range.RowStart, range.RowEnd); } catch { }

            for (int storageColumn = range.ColStart; storageColumn <= range.ColEnd; storageColumn++)
            {
                // Storage column 0 is the grid's row-number column; data columns start at 1. Totalling row
                // numbers is never what was meant, and a whole-row selection always includes it.
                int dataColumn = storageColumn - 1;
                if (dataColumn < 0 || dataColumn >= dataColumnCount)
                    continue;

                if (!accumulators.TryGetValue(dataColumn, out var accumulator))
                {
                    accumulator = new GridAggregateAccumulator();
                    accumulators[dataColumn] = accumulator;
                    order.Add(dataColumn);
                }

                for (long row = range.RowStart; row <= range.RowEnd; row++)
                {
                    bool isNull = false;
                    if (isCellNull != null)
                    {
                        try { isNull = (bool)isCellNull.Invoke(storage, new object[] { row, storageColumn }); }
                        catch { isCellNull = null; }
                    }

                    string value = isNull ? null : storage.GetCellDataAsString(row, storageColumn);

                    // Without QEResultSet's null flag there is no telling a NULL from the literal text
                    // "NULL" — the grid renders them identically, so match what the user sees.
                    if (!isNull && isCellNull == null && value == "NULL")
                        value = null;

                    accumulator.Add(value);
                    combined.Add(value);
                }
            }
        }

        order.Sort();
        var result = new GridAggregateResult();
        foreach (int dataColumn in order)
        {
            result.Columns.Add(accumulators[dataColumn].Build(ColumnName(grid, columnNames, dataColumn), dataColumn));
            result.TotalCells += result.Columns[result.Columns.Count - 1].Cells;
        }

        columnCount = result.Columns.Count;
        if (columnCount > 1)
            result.Combined = combined.Build("All selected", -1);

        return result;
    }

    private static string ColumnName(GridControl grid, StringCollection columnNames, int dataColumn)
    {
        if (columnNames != null && dataColumn < columnNames.Count)
        {
            string name = columnNames[dataColumn];
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        try
        {
            grid.GetHeaderInfo(dataColumn + 1, out string header, out Bitmap _);
            if (!string.IsNullOrEmpty(header))
                return header;
        }
        catch { }
        return $"Column {dataColumn + 1}";
    }
}
