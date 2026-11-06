using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.ScriptCast;

/// <summary>SQLExtended > ScriptCast: opens (or brings forward) the ScriptCast tab.</summary>
internal sealed class ScriptCastCommand
{
    public static readonly Guid CommandSet = new("a1b2c3d4-e5f6-7890-abcd-123456789abc");
    private const int ScriptCastCommandId = 0x0f40;

    private readonly AsyncPackage _package;
    private static ScriptCastCommand _instance;

    private ScriptCastCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, ScriptCastCommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _instance = new ScriptCastCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var window = await _package.ShowToolWindowAsync(typeof(ScriptCastToolWindow), 0, create: true, _package.DisposalToken);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (window?.Frame is IVsWindowFrame frame) Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.SQLExtendedLog.Error("ScriptCast", "Opening ScriptCast failed", ex);
                System.Windows.MessageBox.Show("SQLExtended could not open ScriptCast.\n\n" + ex.Message, "SQLExtended for SSMS", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        });
    }
}
