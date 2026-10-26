using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.Formatting;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// Format Selection (Ctrl+K, Ctrl+Shift+F; issue #8): formats only the statements the selection touches — or the one
/// under the caret — and leaves the rest of the script exactly as it was. <see cref="SelectionFormatter"/> chooses the span
/// and re-indents the result; this class maps between the editor's line/column positions and offsets into the text.
/// Format SQL is unchanged: it still formats a selection as-is, or the whole document.
/// </summary>
internal sealed class FormatSelectionCommand
{
    public const int FormatSelectionCommandId = 0x0250;

    private const string Title = "Format Selection";

    private readonly AsyncPackage _package;

    private FormatSelectionCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(FormatCommand.CommandSet, FormatSelectionCommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new FormatSelectionCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e) =>
        Run(_package, Title, (text, start, end) => SelectionFormatter.Format(text, start, end, FormatterOptions.Load()));

    /// <summary>
    /// Runs a formatter over the active query window: reads the text and the selection as offsets, asks
    /// <paramref name="format"/> for a span and its replacement, and puts it back in one undo step with the result
    /// selected. Shared by Format Selection and Format With… / the format presets.
    /// </summary>
    internal static void Run(AsyncPackage package, string title, Func<string, int, int, SelectionFormatResult> format)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            if (dte?.ActiveDocument?.Object("TextDocument") is not TextDocument textDocument)
            {
                SetStatusBarText($"{title}: no active query window.");
                return;
            }

            var docStart = textDocument.StartPoint.CreateEditPoint();
            string text = docStart.GetText(textDocument.EndPoint);
            var lineStarts = LineStarts(text);

            var selection = textDocument.Selection;
            int selStart = ToOffset(lineStarts, text, selection.TopPoint.Line, selection.TopPoint.LineCharOffset);
            int selEnd = selection.IsEmpty ? selStart : ToOffset(lineStarts, text, selection.BottomPoint.Line, selection.BottomPoint.LineCharOffset);

            var result = format(text, selStart, selEnd);
            if (result == null)
                return;
            if (!result.Success)
            {
                SetStatusBarText($"{title}: {result.ErrorMessage}");
                return;
            }

            var (startLine, startCol) = ToLineCol(lineStarts, result.Start);
            var (endLine, endCol) = ToLineCol(lineStarts, result.End);
            var start = textDocument.CreateEditPoint();
            start.MoveToLineAndOffset(startLine, startCol);
            var end = textDocument.CreateEditPoint();
            end.MoveToLineAndOffset(endLine, endCol);

            if (start.GetText(end) == result.Replacement)
            {
                SetStatusBarText($"{title}: nothing to change.{(result.Note == null ? "" : " " + result.Note)}");
                return;
            }

            dte.UndoContext.Open($"{title} (SQLExtended)");
            try
            {
                start.ReplaceText(end, result.Replacement, 0);
                dte.UndoContext.Close();
            }
            catch
            {
                dte.UndoContext.SetAborted();
                throw;
            }

            // Select what was formatted, so it is plain which statements were taken.
            var (newEndLine, newEndCol) = EndOf(startLine, startCol, result.Replacement);
            selection.MoveToLineAndOffset(startLine, startCol);
            selection.MoveToLineAndOffset(newEndLine, newEndCol, true);

            SetStatusBarText($"{title}: formatted lines {startLine}–{newEndLine}.{(result.Note == null ? "" : " " + result.Note)}");
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("FormatSelection", $"{title} failed", ex);
            VsShellUtilities.ShowMessageBox(package, ex.Message, $"{title} - Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    // Offsets of each line's first character. The editor's LineCharOffset counts characters within the line (a tab is
    // one), so line + column maps onto the text exactly; AbsoluteCharOffset would not, as it counts CRLF as one.
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        return starts;
    }

    private static int ToOffset(List<int> lineStarts, string text, int line, int lineCharOffset)
    {
        int index = Math.Max(0, Math.Min(lineStarts.Count - 1, line - 1));
        return Math.Min(text.Length, lineStarts[index] + Math.Max(0, lineCharOffset - 1));
    }

    private static (int Line, int Col) ToLineCol(List<int> lineStarts, int offset)
    {
        int index = lineStarts.BinarySearch(offset);
        if (index < 0)
            index = ~index - 1;
        return (index + 1, offset - lineStarts[index] + 1);
    }

    private static (int Line, int Col) EndOf(int startLine, int startCol, string inserted)
    {
        int lastNewLine = inserted.LastIndexOf('\n');
        if (lastNewLine < 0)
            return (startLine, startCol + inserted.Length);
        int lines = 0;
        foreach (char c in inserted)
            if (c == '\n')
                lines++;
        return (startLine + lines, inserted.Length - lastNewLine);
    }

    internal static void SetStatusBarText(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            (Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
        }
        catch
        {
            // Status bar not available — ignore
        }
    }
}
