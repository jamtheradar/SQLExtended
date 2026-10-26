using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.ObjectExplorer;

/// <summary>
/// "Locate in Object Explorer" (issue #7) — expands Object Explorer to the object under the cursor, selects it and gives
/// the tree the focus. Bound to SQL Prompt's chord for Select in Object Explorer (Ctrl+F12).
///
/// <para>The name is resolved against the server first (<see cref="SchemaQueryService.ResolveObject"/>), for three things
/// the tree cannot tell us: which schema an unqualified name means, what kind of object it is — which decides the folder
/// it lives in — and, for a trigger, which table it hangs off. It also means "not found" is answered by the server rather
/// than by a tree walk timing out. The walk itself is <see cref="ObjectExplorerHelper.RevealAsync"/>, shared with the
/// validation window.</para>
/// </summary>
internal sealed class LocateInObjectExplorerCommand
{
    public const int CommandId = 0x0120;
    private const string Title = "Locate in Object Explorer";

    private readonly AsyncPackage _package;

    private LocateInObjectExplorerCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(SchemaViewerCommand.CommandSet, CommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new LocateInObjectExplorerCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string objectName = EditorHelper.GetObjectNameAtCursor(_package);
            if (string.IsNullOrWhiteSpace(objectName))
            {
                ShowMessage("Place your cursor on a table, view, procedure, function or trigger name, or select the name.");
                return;
            }

            string connectionString = ConnectionHelper.GetActiveConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                ShowMessage("No active database connection found. Open a connected query window first.");
                return;
            }

            // The window's own UIConnectionInfo, to connect Object Explorer with if the server is not in it yet.
            object uiConnectionInfo = ServiceCacheProxy.GetActiveUIConnectionInfo();
            string server;
            try { server = ConnectionHelper.NormalizeHarvestedDataSource(new SqlConnectionStringBuilder(connectionString).DataSource); }
            catch { server = ""; }

            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                SchemaQueryService.ResolvedObject resolved = null;
                Exception failure = null;
                try
                {
                    resolved = await Task.Run(() => SchemaQueryService.ResolveObject(connectionString, objectName));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (failure != null)
                {
                    ShowMessage($"Could not look up '{objectName}': {failure.Message}");
                    return;
                }
                if (resolved == null)
                {
                    ShowMessage($"'{objectName}' was not found on {server}, or you do not have permission to see it.");
                    return;
                }

                string label = resolved.Type == "TR" ? $"trigger {resolved.Name} on {resolved.ParentSchema}.{resolved.ParentName}" : $"{resolved.Schema}.{resolved.Name}";
                SetStatusBar($"Locating {label} in Object Explorer...");

                var outcome = await ObjectExplorerHelper.RevealAsync(server, resolved, uiConnectionInfo, strictServer: true, activate: true);
                switch (outcome)
                {
                    case ObjectExplorerHelper.RevealOutcome.Found:
                        SetStatusBar($"Located {label} in Object Explorer.");
                        break;
                    case ObjectExplorerHelper.RevealOutcome.Partial:
                        SetStatusBar($"Could not find {label} in Object Explorer; selected the nearest folder. A filter on that folder can hide it.");
                        break;
                    case ObjectExplorerHelper.RevealOutcome.DatabaseNotFound:
                        ShowMessage($"Database '{resolved.Database}' was not found under {server} in Object Explorer.");
                        break;
                    case ObjectExplorerHelper.RevealOutcome.ServerNotConnected:
                        ShowMessage($"{server} is not connected in Object Explorer, and connecting it failed. Connect it and try again.");
                        break;
                    default:
                        ShowMessage("Object Explorer is not available.");
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private static void SetStatusBar(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { if (Package.GetGlobalService(typeof(SDTE)) is EnvDTE.DTE dte) dte.StatusBar.Text = text; } catch { }
    }

    private void ShowMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(_package, message, Title,
            OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
