using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.Align;

/// <summary>
/// Align On (Ctrl+=, issue #8): asks for a token and lines it up across the selected lines with <see cref="ColumnAligner"/>.
/// With no selection it works on the block around the caret — the run of non-blank lines — which is nearly always the
/// statement meant, and is what CodeAlignment (the extension the issue points to) does.
/// </summary>
internal sealed class AlignOnCommand
{
    public const int AlignOnCommandId = 0x0240;

    private const string Title = "Align On";

    private readonly AsyncPackage _package;

    private AlignOnCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(FormatCommand.CommandSet, AlignOnCommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new AlignOnCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
            if (dte?.ActiveDocument?.Object("TextDocument") is not TextDocument textDocument)
            {
                SetStatusBarText($"{Title}: no active query window.");
                return;
            }

            var selection = textDocument.Selection;
            int top, bottom;
            if (selection.IsEmpty)
            {
                (top, bottom) = BlockAround(textDocument, selection.ActivePoint.Line);
            }
            else
            {
                top = selection.TopPoint.Line;
                bottom = selection.BottomPoint.Line;
                // A selection made by whole lines ends at the start of the next one, which is not part of it.
                if (bottom > top && selection.BottomPoint.LineCharOffset == 1)
                    bottom--;
            }

            if (bottom <= top)
            {
                SetStatusBarText($"{Title}: select two or more lines to align.");
                return;
            }

            var window = new AlignOnWindow();
            if (window.ShowDialog() != true || string.IsNullOrEmpty(window.Token))
                return;

            var start = textDocument.CreateEditPoint();
            start.MoveToLineAndOffset(top, 1);
            var end = textDocument.CreateEditPoint();
            end.MoveToLineAndOffset(bottom, 1);
            end.EndOfLine();

            string text = start.GetText(end);
            var (useTabs, tabSize) = EditorTabSettings();
            var result = ColumnAligner.Align(text, window.Token, window.EveryOccurrence, useTabs, tabSize);
            if (!result.Changed)
            {
                SetStatusBarText($"{Title}: nothing to align on \"{window.Token}\" — it needs to appear on two or more of the lines, outside strings and comments.");
                return;
            }

            dte.UndoContext.Open($"Align On {window.Token} (SQLExtended)");
            try
            {
                start.ReplaceText(end, result.Text, 0);
                dte.UndoContext.Close();
            }
            catch
            {
                dte.UndoContext.SetAborted();
                throw;
            }

            // Leave the block selected, so the next token (AS, then ON, then =) is one keystroke away.
            selection.MoveToLineAndOffset(top, 1);
            selection.MoveToLineAndOffset(bottom, 1, true);
            selection.EndOfLine(true);

            SetStatusBarText($"{Title}: aligned \"{window.Token}\" on {result.LinesAligned} line{(result.LinesAligned == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("AlignOn", "Align On failed", ex);
            VsShellUtilities.ShowMessageBox(_package, ex.Message, $"{Title} - Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    // The run of non-blank lines around the caret.
    private static (int Top, int Bottom) BlockAround(TextDocument document, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var point = document.CreateEditPoint();
        bool Blank(int n) { point.MoveToLineAndOffset(n, 1); return string.IsNullOrWhiteSpace(point.GetLines(n, n + 1)); }

        if (Blank(line))
            return (line, line);
        int top = line, bottom = line, last = document.EndPoint.Line;
        while (top > 1 && !Blank(top - 1)) top--;
        while (bottom < last && !Blank(bottom + 1)) bottom++;
        return (top, bottom);
    }

    /// <summary>
    /// The active editor's own Tabs setting (Tools > Options > Text Editor), read from the WPF view's options so it is
    /// the value in force for this window. Falls back to spaces and 4 if the view cannot be reached.
    /// </summary>
    private static (bool UseTabs, int TabSize) EditorTabSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(SVsTextManager)) is IVsTextManager textManager
                && textManager.GetActiveView(1, null, out IVsTextView vsView) == 0 && vsView != null
                && Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel components
                && components.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsView) is { } view)
            {
                return (!view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId), view.Options.GetOptionValue(DefaultOptions.TabSizeOptionId));
            }
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("AlignOn", "Could not read the editor's tab settings; using spaces", ex);
        }
        return (false, 4);
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
