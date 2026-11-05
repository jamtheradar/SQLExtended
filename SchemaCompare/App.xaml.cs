using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Shell;

public partial class App : Application
{
    public static readonly string ErrorLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SchemaCompare", "error.log");

    /// <summary><c>SchemaCompare.exe [left] [right]</c>: with both paths given, the compare runs straight away.</summary>
    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;

        // The palette goes in before the first window is built, so nothing paints in the wrong theme first.
        var settings = CompareSettings.Load<ShellSettings>();
        AppTheme.Initialize(settings.Theme);
        CompareHost.Current = new ShellCompareHost(settings);

        var window = new MainWindow();
        window.Show();
        if (e.Args.Length >= 2) window.View.StartCompare(e.Args[0], e.Args[1]);
        else if (e.Args.Length == 1) window.View.SetLeft(e.Args[0]);
    }

    /// <summary>A bug in one view shouldn't take the compare with it: log it, say so, carry on.</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog));
            File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nowhere to write it; the message box still says what happened.
        }

        MessageBox.Show(MainWindow, e.Exception.Message + Environment.NewLine + Environment.NewLine + "Details: " + ErrorLog, "Schema Compare", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
