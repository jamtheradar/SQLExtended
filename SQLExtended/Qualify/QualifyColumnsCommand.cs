using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.IntelliSense;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.Qualify;

/// <summary>
/// Qualify Columns (Ctrl+B, Ctrl+Q): aliases every table in the selection's (or the document's) SELECTs and prefixes
/// each column with the alias of the table it belongs to, using the schema cache to know which table that is.
/// </summary>
internal sealed class QualifyColumnsCommand
{
    public const int QualifyColumnsCommandId = 0x0230;

    private const string Title = "Qualify Columns";

    private readonly AsyncPackage _package;

    private QualifyColumnsCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(FormatCommand.CommandSet, QualifyColumnsCommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new QualifyColumnsCommand(package, commandService);
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

            var context = ColumnLookupContext.TryCreate(out string notReady);
            if (context == null)
            {
                SetStatusBarText($"{Title}: {notReady}");
                return;
            }

            var selection = textDocument.Selection;
            bool hasSelection = !selection.IsEmpty;
            var docStart = textDocument.StartPoint.CreateEditPoint();
            string documentText = docStart.GetText(textDocument.EndPoint);
            string sql = hasSelection ? selection.Text : documentText;
            if (string.IsNullOrWhiteSpace(sql))
                return;

            var localTables = LocalTableScanner.Scan(documentText);
            var result = ColumnQualifier.Qualify(sql, t => context.Lookup(t, localTables));
            if (!result.Success)
            {
                SetStatusBarText($"{Title}: {result.ErrorMessage}.");
                return;
            }

            if (result.Sql != sql)
            {
                dte.UndoContext.Open("Qualify Columns (SQLExtended)");
                try
                {
                    if (hasSelection)
                    {
                        selection.Delete();
                        selection.Insert(result.Sql);
                    }
                    else
                    {
                        docStart.ReplaceText(textDocument.EndPoint, result.Sql, 0);
                    }
                    dte.UndoContext.Close();
                }
                catch
                {
                    dte.UndoContext.SetAborted();
                    throw;
                }
            }

            SetStatusBarText(Summary(result));
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("QualifyColumns", "Qualify Columns failed", ex);
            VsShellUtilities.ShowMessageBox(_package, ex.Message, $"{Title} - Error", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }

    private static string Summary(ColumnQualifierResult result)
    {
        if (result.TablesAliased == 0 && result.ColumnsQualified == 0)
            return result.ColumnsLeft == 0
                ? $"{Title}: nothing to change."
                : $"{Title}: nothing changed — {Plural(result.ColumnsLeft, "column")} could not be matched to exactly one table.";

        string done = $"{Title}: aliased {Plural(result.TablesAliased, "table")}, qualified {Plural(result.ColumnsQualified, "column")}.";
        return result.ColumnsLeft == 0 ? done : $"{done} {Plural(result.ColumnsLeft, "column")} left as written (not found, ambiguous, or from a source with unknown columns).";
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

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
