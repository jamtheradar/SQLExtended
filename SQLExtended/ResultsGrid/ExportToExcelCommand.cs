using EnvDTE;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SQLExtended.ResultsGrid.Aggregates;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Reflection;
using Task = System.Threading.Tasks.Task;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;

namespace SQLExtended.ResultsGrid;

/// <summary>
/// "Export to Excel..." — writes the cells selected in a results grid (cells, rows, columns, Select All) to a new .xlsx,
/// with the column names as a header row and cells typed by SQL type (<see cref="XlsxWriter"/>). It sits directly under
/// SSMS's own "Save Results As..." in the grid's right-click menu, in SQLEditors' <c>IDG_SQLWB_SAVEPOPUP</c> group — which
/// SSMS also places on the Query menu, so it appears there too — and in the SQLExtended menu.
///
/// <para>The selection is resolved by <see cref="GridSelectionReader.ResolveRanges"/>, as Copy as IN Filter's is. Several
/// Ctrl+Click blocks are exported as one table: every selected row by every selected column, in grid order and on-screen
/// column order, with a cell that no block covers left empty. Cells are read on the UI thread (the grid is a WinForms
/// control); only the file is written in the background.</para>
/// </summary>
internal sealed class ExportToExcelCommand
{
    private const int CommandId = 0x0c50;

    /// <summary>Cells read on the UI thread before the export refuses. Reading is one grid call per cell.</summary>
    internal const long MaxCells = 2_000_000;

    private static string _lastFolder;
    private readonly AsyncPackage _package;

    private ExportToExcelCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        var command = new OleMenuCommand(Execute, new CommandID(Find.GridFindCommand.CommandSet, CommandId));
        command.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new ExportToExcelCommand(package, commandService);
    }

    /// <summary>Shown only where there is a grid to export, as "Save Results As..." beside it is — the group is also on the Query menu.</summary>
    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not OleMenuCommand command) return;
        try
        {
            command.Visible = ResultsGridReader.GetFocusedGrid() != null || ResultsGridReader.FindActiveDocumentGrids().Count > 0;
        }
        catch
        {
            command.Visible = true;
        }
        command.Enabled = true;
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            GridControl grid = ResultsGridReader.GetFocusedGrid();
            if (grid == null)
            {
                // From the menus the grid may no longer hold the focus; one grid in the window is unambiguous.
                var grids = ResultsGridReader.FindActiveDocumentGrids();
                if (grids.Count == 1) grid = grids[0];
            }
            if (grid == null)
            {
                SetStatusBar("Export to Excel: click into the results grid and select the cells to export first.");
                return;
            }

            if (!TryReadSelection(grid, out ResultGridData data, out string refusal))
            {
                SetStatusBar("Export to Excel: " + refusal);
                return;
            }

            string path = AskForPath();
            if (path == null) return;

            _package.JoinableTaskFactory.RunAsync(() => WriteAsync(path, data)).FileAndForget("SQLExtended/ExportToExcel");
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ExportToExcel", "Export to Excel failed", ex);
            SetStatusBar($"Export to Excel failed: {ex.Message}");
        }
    }

    private async Task WriteAsync(string path, ResultGridData data)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        SetStatusBar($"Exporting {data.Rows.Count:N0} row(s) to {Path.GetFileName(path)}...");

        XlsxWriteResult result;
        try
        {
            await TaskScheduler.Default;
            // Written to a temp file first, so a failure part way never leaves a corrupt workbook at the chosen path.
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                result = XlsxWriter.Write(stream, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch (Exception ex)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Diagnostics.SQLExtendedLog.Warning("ExportToExcel", "Writing the workbook failed", ex);
            string hint = ex is IOException ? " If the file is open in Excel, close it and try again." : "";
            SetStatusBar("Export to Excel failed.");
            ShowMessage($"Could not write {path}:\n\n{ex.Message}{hint}", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK);
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        string summary = $"Exported {result.Rows:N0} row{(result.Rows == 1 ? "" : "s")} and {result.Columns:N0} column{(result.Columns == 1 ? "" : "s")} to {Path.GetFileName(path)}.";
        SetStatusBar(summary);

        string truncated = result.TruncatedCells > 0
            ? $"\n\n{result.TruncatedCells:N0} cell(s) were longer than Excel's {XlsxWriter.MaxCellText:N0}-character limit and were cut to it."
            : "";
        int answer = ShowMessage($"{summary}{truncated}\n\nOpen it now?", truncated.Length > 0 ? OLEMSGICON.OLEMSGICON_WARNING : OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO);
        if (answer == 6 /* IDYES */)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatusBar($"Could not open {Path.GetFileName(path)}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The selection as a table: column names and SQL types of the selected columns in on-screen order, and one row per
    /// selected row. False with a reason when nothing is selected or the selection is over <see cref="MaxCells"/> or
    /// Excel's sheet size — refused whole rather than cut, since a spreadsheet missing rows says nothing about it.
    /// </summary>
    private static bool TryReadSelection(GridControl grid, out ResultGridData data, out string refusal)
    {
        data = null;
        refusal = null;

        IGridStorage storage = grid.GridStorage;
        BlockOfCellsCollection blocks = grid.SelectedCells;
        long numRows = storage?.NumRows() ?? 0;
        int numCols = grid.ColumnsNumber;
        if (storage == null || blocks == null || blocks.Count == 0 || numRows <= 0 || numCols <= 1)
        {
            refusal = "select the cells, rows or columns to export first (Ctrl+A for the whole grid).";
            return false;
        }

        var ranges = GridSelectionReader.ResolveRanges(grid, blocks, numRows, numCols);

        // Storage column 0 is the row-number column; a whole-row selection includes it, and it is never wanted.
        var columnSet = new HashSet<int>();
        long rowUpperBound = 0;
        foreach (var range in ranges)
        {
            for (int c = Math.Max(1, range.ColStart); c <= range.ColEnd; c++)
                columnSet.Add(c);
            rowUpperBound += range.RowEnd - range.RowStart + 1;
        }
        if (columnSet.Count == 0 || rowUpperBound <= 0)
        {
            refusal = "select the cells, rows or columns to export first (Ctrl+A for the whole grid).";
            return false;
        }

        // Sized before anything is read. Overlapping blocks make the row count an upper bound, the safe side for a cap.
        if (rowUpperBound * columnSet.Count > MaxCells)
        {
            refusal = $"{rowUpperBound * columnSet.Count:N0} cells are selected, over the {MaxCells:N0}-cell limit. Nothing was exported — use Save Results As for a grid this size.";
            return false;
        }

        var seen = new SortedSet<long>();
        foreach (var range in ranges)
            for (long r = range.RowStart; r <= range.RowEnd; r++)
                seen.Add(r);
        if (seen.Count + 1L > XlsxWriter.MaxRows)
        {
            refusal = $"{seen.Count:N0} rows are selected; an Excel sheet holds {XlsxWriter.MaxRows - 1:N0} below the header. Nothing was exported.";
            return false;
        }

        int[] columns = columnSet.OrderBy(c => UiIndex(grid, c)).ToArray();
        List<long> rows = seen.ToList();
        bool rectangular = ranges.Count == 1;

        data = new ResultGridData { ColumnNames = new string[columns.Length], SqlTypes = new string[columns.Length] };
        Type storageType = storage.GetType();
        StringCollection colNames = null;
        MethodInfo typeName = null;
        Func<long, int, bool> isNull = null;
        try { colNames = storageType.GetProperty("ColumnNames", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(storage) as StringCollection; } catch { }
        try { typeName = storageType.GetMethod("GetFormattedDataTypeName", new[] { typeof(int) }); } catch { }
        try
        {
            // A delegate rather than MethodInfo.Invoke: this is called once per cell, up to MaxCells times.
            MethodInfo m = storageType.GetMethod("IsCellDataNull", new[] { typeof(long), typeof(int) });
            if (m != null) isNull = (Func<long, int, bool>)Delegate.CreateDelegate(typeof(Func<long, int, bool>), storage, m);
        }
        catch { }

        for (int i = 0; i < columns.Length; i++)
        {
            int c = columns[i];
            if (colNames != null && c - 1 < colNames.Count)
                data.ColumnNames[i] = colNames[c - 1];
            else
            {
                try { grid.GetHeaderInfo(c, out string header, out System.Drawing.Bitmap _); data.ColumnNames[i] = header; } catch { }
            }
            try { data.SqlTypes[i] = typeName?.Invoke(storage, new object[] { c - 1 }) as string; } catch { }
        }

        try { storage.EnsureRowsInBuf(rows[0], rows[rows.Count - 1]); } catch { }

        foreach (long r in rows)
        {
            var row = new string[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                int c = columns[i];
                if (!rectangular && !Covers(ranges, r, c)) continue;
                bool cellIsNull = false;
                if (isNull != null)
                {
                    try { cellIsNull = isNull(r, c); } catch { isNull = null; }
                }
                string value = cellIsNull ? null : storage.GetCellDataAsString(r, c);
                if (!cellIsNull && isNull == null && value == "NULL")
                    value = null;
                row[i] = value;
            }
            data.Rows.Add(row);
        }
        return true;
    }

    private static bool Covers(List<GridSelectionReader.CellRange> ranges, long row, int column)
    {
        foreach (var range in ranges)
            if (row >= range.RowStart && row <= range.RowEnd && column >= range.ColStart && column <= range.ColEnd)
                return true;
        return false;
    }

    private static int UiIndex(GridControl grid, int storageColumn)
    {
        try { return grid.GetUIColumnIndexByStorageIndex(storageColumn); }
        catch { return storageColumn; }
    }

    private static string AskForPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to Excel",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Results_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            InitialDirectory = _lastFolder != null && Directory.Exists(_lastFolder) ? _lastFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() != true) return null;
        _lastFolder = Path.GetDirectoryName(dialog.FileName);
        return dialog.FileName;
    }

    private int ShowMessage(string message, OLEMSGICON icon, OLEMSGBUTTON buttons)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsShellUtilities.ShowMessageBox(_package, message, "SQLExtended", icon, buttons, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private static void SetStatusBar(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { if (Package.GetGlobalService(typeof(SDTE)) is DTE dte) dte.StatusBar.Text = text; } catch { }
    }
}
