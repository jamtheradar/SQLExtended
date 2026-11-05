using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.SchemaCompare.Ssms;

/// <summary>SQLExtended > Schema Compare: opens (or brings forward) the compare tab.</summary>
internal sealed class SchemaCompareCommand
{
    public static readonly Guid CommandSet = new("a1b2c3d4-e5f6-7890-abcd-123456789abc");
    private const int SchemaCompareCommandId = 0x0f30;

    private readonly AsyncPackage _package;
    private static SchemaCompareCommand _instance;

    private SchemaCompareCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;
        commandService.AddCommand(new MenuCommand(Execute, new CommandID(CommandSet, SchemaCompareCommandId)));
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            _instance = new SchemaCompareCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _ = _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var window = await _package.ShowToolWindowAsync(typeof(SchemaCompareToolWindow), 0, create: true, _package.DisposalToken);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (window?.Frame is IVsWindowFrame frame) Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                // Fire-and-forget: a failure here (a missing AvalonEdit, a bad XAML resource) would otherwise vanish.
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                Diagnostics.SQLExtendedLog.Error("SchemaCompare", "Opening Schema Compare failed", ex);
                System.Windows.MessageBox.Show("SQLExtended could not open Schema Compare.\n\n" + ex.Message, "SQLExtended for SSMS", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        });
    }
}
