using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.Formatting;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// Command handler for Format SQL (Ctrl+K, Ctrl+Y) and Format Options.
/// Formats selected text or entire document using ScriptDom.
/// </summary>
internal sealed class FormatCommand
{
    // Command IDs — must match .vsct
    public const int FormatSqlCommandId = 0x0200;
    public const int FormatOptionsCommandId = 0x0210;

    public static readonly Guid CommandSet = new Guid("a1b2c3d4-e5f6-7890-abcd-123456789abc");

    private readonly AsyncPackage _package;
    private static FormatCommand _instance;

    private FormatCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));

        // Register Format SQL command
        var formatId = new CommandID(CommandSet, FormatSqlCommandId);
        var formatItem = new MenuCommand(ExecuteFormat, formatId);
        commandService.AddCommand(formatItem);

        // Register Format Options command
        var optionsId = new CommandID(CommandSet, FormatOptionsCommandId);
        var optionsItem = new MenuCommand(ExecuteOptions, optionsId);
        commandService.AddCommand(optionsItem);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as OleMenuCommandService;

        _instance = new FormatCommand(package, commandService);
    }

    /// <summary>
    /// Formats the selected SQL text or entire document.
    /// </summary>
    private void ExecuteFormat(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var dte = (DTE2)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
            if (dte?.ActiveDocument == null)
            {
                ShowMessage("Format SQL", "No active document.");
                return;
            }

            var textDocument = dte.ActiveDocument.Object("TextDocument") as TextDocument;
            if (textDocument == null)
            {
                ShowMessage("Format SQL", "Active document is not a text document.");
                return;
            }

            var selection = textDocument.Selection;
            bool hasSelection = !selection.IsEmpty;
            string inputSql;
            EditPoint startPoint;
            EditPoint endPoint;

            if (hasSelection)
            {
                inputSql = selection.Text;
                startPoint = selection.TopPoint.CreateEditPoint();
                endPoint = selection.BottomPoint.CreateEditPoint();
            }
            else
            {
                // Format entire document
                startPoint = textDocument.StartPoint.CreateEditPoint();
                endPoint = textDocument.EndPoint.CreateEditPoint();
                inputSql = startPoint.GetText(endPoint);
            }

            if (string.IsNullOrWhiteSpace(inputSql))
                return;

            // Load options and format
            var options = FormatterOptions.Load();
            var formatter = new SqlFormatterService(options);
            var result = formatter.Format(inputSql);

            if (!result.Success)
            {
                // Show error in status bar but don't modify the text
                SetStatusBarText($"Format SQL: {result.ErrorMessage}");
                return;
            }

            // Replace text in editor using an undo context so user can Ctrl+Z
            dte.UndoContext.Open("Format SQL (SQLExtended)");
            try
            {
                if (hasSelection)
                {
                    selection.Delete();
                    selection.Insert(result.FormattedSql);
                }
                else
                {
                    startPoint.ReplaceText(endPoint, result.FormattedSql, (int)vsEPReplaceTextOptions.vsEPReplaceTextAutoformat);
                }

                dte.UndoContext.Close();
            }
            catch
            {
                dte.UndoContext.SetAborted();
                throw;
            }

            SetStatusBarText("SQL formatted successfully.");
        }
        catch (Exception ex)
        {
            ShowMessage("Format SQL - Error", ex.Message);
        }
    }

    /// <summary>
    /// Opens the unified SQLExtended Settings dialog (Formatter tab is accessible from there).
    /// Also opens the full formatter options dialog for direct access.
    /// </summary>
    private void ExecuteOptions(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            // Try to grab the current editor text for the "Current Document" preview
            string currentDocumentSql = null;
            try
            {
                var dte = (DTE2)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
                if (dte?.ActiveDocument != null)
                {
                    var textDocument = dte.ActiveDocument.Object("TextDocument") as TextDocument;
                    if (textDocument != null)
                    {
                        var start = textDocument.StartPoint.CreateEditPoint();
                        currentDocumentSql = start.GetText(textDocument.EndPoint);
                    }
                }
            }
            catch
            {
                // No document available — dialog will disable the toggle
            }

            var options = FormatterOptions.Load();
            var dialog = new FormatterOptionsDialog(options, currentDocumentSql);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowMessage("Format Options - Error", ex.Message);
        }
    }

    private void ShowMessage(string title, string message)
    {
        VsShellUtilities.ShowMessageBox(
            _package, message, title,
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private void SetStatusBarText(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var statusBar = (IVsStatusbar)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsStatusbar));
            statusBar?.SetText(text);
        }
        catch
        {
            // Status bar not available — ignore
        }
    }
}
