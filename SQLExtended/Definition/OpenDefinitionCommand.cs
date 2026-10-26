using EnvDTE;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.Settings;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.Definition;

/// <summary>
/// "Open Definition" (F12, issue #6) — scripts the object under the cursor straight into a new query window on the same
/// server and database, skipping the View Schema preview. Visual Studio's Go to Definition, for T-SQL. The preview's
/// "Open in Script Window" button ends up in <see cref="OpenScript"/> too.
///
/// <para>The script is View Schema's own (<see cref="SchemaQueryService.GetSchemaScript(string, string)"/>), so a table
/// comes with its indexes and foreign keys and an encrypted module is decrypted when that is switched on. What this adds
/// is a <c>USE</c> for the database and the module's <c>CREATE</c> rewritten to the chosen
/// <see cref="DefinitionScriptMode"/> — see <see cref="DefinitionScript"/>.</para>
/// </summary>
internal sealed class OpenDefinitionCommand
{
    public const int CommandId = 0x0110;
    private const string Title = "Open Definition";

    private readonly AsyncPackage _package;

    private OpenDefinitionCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(SchemaViewerCommand.CommandSet, CommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _ = new OpenDefinitionCommand(package, commandService);
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

            // Taken now, while the source window is still the active one: the new window is opened on this connection.
            object uiConnectionInfo = ServiceCacheProxy.GetActiveUIConnectionInfo();
            var mode = SQLExtendedSettings.Load().DefinitionScriptMode;

            // Off the UI thread for the same reason View Schema is: the script can mean decrypting a module over a DAC.
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                SchemaQueryService.ResolvedObject resolved = null;
                string script = null;
                Exception failure = null;
                try
                {
                    await Task.Run(() =>
                    {
                        resolved = SchemaQueryService.ResolveObject(connectionString, objectName);
                        if (resolved != null)
                            script = SchemaQueryService.GetSchemaScript(connectionString, resolved.QualifiedName);
                    });
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (failure != null)
                    ShowMessage($"Could not script '{objectName}': {failure.Message}");
                else if (resolved == null)
                    ShowMessage($"'{objectName}' was not found in {DatabaseOf(connectionString, objectName)}, or you do not have permission to see it.");
                else if (string.IsNullOrWhiteSpace(script))
                    ShowMessage($"'{resolved.Schema}.{resolved.Name}' has no definition to script (a synonym, or you lack VIEW DEFINITION on it).");
                else
                    Open(DefinitionScript.Build(resolved.Database, script, mode), uiConnectionInfo, ServerOf(connectionString));
            });
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>
    /// Opens a script from the View Schema preview. <paramref name="connectionString"/> is the one the preview was built
    /// from, which is not necessarily the active window's — SQL Search and the validation window open previews for any
    /// server — so the active window's connection is used only when it is on the same server. The database is pinned by
    /// the <c>USE</c> either way.
    /// </summary>
    public static void OpenScript(string script, string connectionString, string objectName, bool applyMode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string database = EditorHelper.ParseObjectName(objectName).Database ?? DatabaseOf(connectionString, null);
        string text = DefinitionScript.Build(database, script, applyMode ? SQLExtendedSettings.Load().DefinitionScriptMode : null);

        object uiConnectionInfo = ServiceCacheProxy.GetActiveUIConnectionInfo();
        string server = ServerOf(connectionString);
        if (uiConnectionInfo != null && !string.IsNullOrEmpty(server) && !SameServer(ServerOf(ServiceCacheProxy.BuildConnectionString(uiConnectionInfo)), server))
            uiConnectionInfo = null;

        Open(text, uiConnectionInfo, server);
    }

    private static void Open(string text, object uiConnectionInfo, string server)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!QueryWindowOpener.Open(text, uiConnectionInfo, "Definition"))
            SetStatusBar($"Open Definition: the new window may not be connected to {server} - check its connection before running it.");
    }

    private static string ServerOf(string connectionString)
    {
        try { return ConnectionHelper.NormalizeHarvestedDataSource(new SqlConnectionStringBuilder(connectionString).DataSource); }
        catch { return ""; }
    }

    private static string DatabaseOf(string connectionString, string objectName)
    {
        string named = objectName == null ? null : EditorHelper.ParseObjectName(objectName).Database;
        if (!string.IsNullOrEmpty(named)) return named;
        try { return new SqlConnectionStringBuilder(connectionString).InitialCatalog; }
        catch { return null; }
    }

    private static bool SameServer(string a, string b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static void SetStatusBar(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { if (Package.GetGlobalService(typeof(SDTE)) is DTE dte) dte.StatusBar.Text = text; } catch { }
    }

    private void ShowMessage(string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(_package, message, Title,
            OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
