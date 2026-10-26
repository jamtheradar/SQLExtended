using EnvDTE;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.ResultsGrid.Aggregates;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Reflection;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.ResultsGrid;

/// <summary>
/// "Copy as IN Filter" (Ctrl+Shift+I, issue #5) — copies the cells selected in one column of a results grid as an
/// <c>IN (...)</c> list, ready to paste into a WHERE clause. Shift+Click, Ctrl+Click and whole-column selections all work;
/// values come out in grid order, NULLs are left out, and quoting follows the column's type (<see cref="InFilterBuilder"/>).
///
/// <para>The selection is resolved by <see cref="GridSelectionReader.ResolveRanges"/>, the aggregates pane's reader, which
/// owns the two traps the grid sets — whole-column blocks whose Y/Bottom mean nothing, and column indexes that are UI
/// indexes for some selection types and storage indexes for others. A selection spanning more than one column is
/// refused rather than flattened: <c>IN</c> filters one column, and a list mixing ids with names pastes without error.</para>
/// </summary>
internal sealed class CopyAsInFilterCommand
{
    private const int CommandId = 0x0c40;

    private CopyAsInFilterCommand(OleMenuCommandService commandService)
    {
        var command = new OleMenuCommand(Execute, new CommandID(Find.GridFindCommand.CommandSet, CommandId));
        command.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new CopyAsInFilterCommand(commandService);
    }

    /// <summary>
    /// Disabled while the focused grid's selection spans several columns. With no grid focused it stays enabled, so that
    /// the SQLExtended menu can still run it and say what is wrong — a greyed item explains nothing.
    /// </summary>
    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not OleMenuCommand command) return;
        try
        {
            GridControl grid = ResultsGridReader.GetFocusedGrid();
            command.Enabled = grid == null || ReadSelection(grid, collectRows: false, out _, out _) != SelectionShape.ManyColumns;
        }
        catch
        {
            command.Enabled = true;
        }
    }

    private enum SelectionShape { Empty, OneColumn, ManyColumns }

    /// <summary>
    /// The selected data column (storage index) and, when <paramref name="collectRows"/>, its selected rows ascending and
    /// without repeats — null when there are more than the cap. Query status only needs the shape, and is asked often.
    /// </summary>
    private static SelectionShape ReadSelection(GridControl grid, bool collectRows, out int storageColumn, out List<long> rows)
    {
        storageColumn = -1;
        rows = new List<long>();

        IGridStorage storage = grid.GridStorage;
        BlockOfCellsCollection blocks = grid.SelectedCells;
        if (storage == null || blocks == null || blocks.Count == 0) return SelectionShape.Empty;

        long numRows = storage.NumRows();
        int numCols = grid.ColumnsNumber;
        if (numRows <= 0 || numCols <= 0) return SelectionShape.Empty;

        var ranges = GridSelectionReader.ResolveRanges(grid, blocks, numRows, numCols);

        // Storage column 0 is the row-number column. A whole-row selection includes it alongside every data column, so it
        // reads as many columns, as it should.
        var columns = new HashSet<int>();
        foreach (var range in ranges)
        {
            for (int c = range.ColStart; c <= range.ColEnd; c++)
                columns.Add(c);
        }
        columns.Remove(0);
        if (columns.Count == 0) return SelectionShape.Empty;
        if (columns.Count > 1) return SelectionShape.ManyColumns;

        storageColumn = columns.First();
        if (!collectRows) return SelectionShape.OneColumn;
        var seen = new SortedSet<long>();
        foreach (var range in ranges)
        {
            // Bounded before it is filled: a whole column of a million-row grid is a million entries otherwise.
            if (range.RowEnd - range.RowStart + 1 + seen.Count > ResultsGridReader.MaxRows)
            {
                rows = null;
                return SelectionShape.OneColumn;
            }
            for (long r = range.RowStart; r <= range.RowEnd; r++)
                seen.Add(r);
        }
        rows = seen.ToList();
        return SelectionShape.OneColumn;
    }

    private static void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            GridControl grid = ResultsGridReader.GetFocusedGrid();
            if (grid == null)
            {
                // From the main menu the grid may no longer hold the focus; one grid in the window is unambiguous.
                var grids = ResultsGridReader.FindActiveDocumentGrids();
                if (grids.Count == 1) grid = grids[0];
            }
            if (grid == null)
            {
                SetStatusBar("Copy as IN Filter: click into the results grid and select cells in one column first.");
                return;
            }

            switch (ReadSelection(grid, collectRows: true, out int storageColumn, out List<long> rows))
            {
                case SelectionShape.Empty:
                    SetStatusBar("Copy as IN Filter: select one or more cells in a data column first.");
                    return;
                case SelectionShape.ManyColumns:
                    SetStatusBar("Copy as IN Filter: the selection spans more than one column. Select cells in a single column.");
                    return;
            }

            // Refused rather than truncated: a filter missing values matches fewer rows and nothing says so.
            if (rows == null)
            {
                SetStatusBar($"Copy as IN Filter: more than {ResultsGridReader.MaxRows:N0} rows are selected. Nothing was copied.");
                return;
            }

            IGridStorage storage = grid.GridStorage;
            ReadCells(storage, storageColumn, rows, out List<string> values, out string sqlType);

            string text = InFilterBuilder.Build(values, sqlType);
            if (text == null)
            {
                SetStatusBar("Copy as IN Filter: every selected cell is NULL. Nothing was copied.");
                return;
            }

            System.Windows.Clipboard.SetText(text);
            int copied = values.Count(v => v != null);
            int nulls = values.Count - copied;
            SetStatusBar($"Copied an IN filter of {copied:N0} value{(copied == 1 ? "" : "s")}" + (nulls > 0 ? $" ({nulls:N0} NULL{(nulls == 1 ? "" : "s")} left out)." : "."));
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("CopyAsInFilter", "Copy as IN Filter failed", ex);
            SetStatusBar($"Copy as IN Filter failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The cells' display text, null for a NULL, and the column's SQL type — both from QEResultSet by reflection as
    /// <see cref="ResultsGridReader.Read"/> does, with the grid's "NULL" rendering as the fallback for nulls.
    /// </summary>
    private static void ReadCells(IGridStorage storage, int storageColumn, List<long> rows, out List<string> values, out string sqlType)
    {
        Type storageType = storage.GetType();
        MethodInfo isCellNull = null;
        sqlType = null;
        try { isCellNull = storageType.GetMethod("IsCellDataNull", new[] { typeof(long), typeof(int) }); } catch { }
        try { sqlType = storageType.GetMethod("GetFormattedDataTypeName", new[] { typeof(int) })?.Invoke(storage, new object[] { storageColumn - 1 }) as string; } catch { }

        if (rows.Count > 0)
        {
            try { storage.EnsureRowsInBuf(rows[0], rows[rows.Count - 1]); } catch { }
        }

        values = new List<string>(rows.Count);
        foreach (long row in rows)
        {
            bool isNull = false;
            if (isCellNull != null)
            {
                try { isNull = (bool)isCellNull.Invoke(storage, new object[] { row, storageColumn }); }
                catch { isCellNull = null; }
            }
            string value = isNull ? null : storage.GetCellDataAsString(row, storageColumn);
            if (!isNull && isCellNull == null && value == "NULL")
                value = null;
            values.Add(value);
        }
    }

    private static void SetStatusBar(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { if (Package.GetGlobalService(typeof(SDTE)) is DTE dte) dte.StatusBar.Text = text; } catch { }
    }
}
