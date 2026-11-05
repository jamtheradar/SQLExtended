using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Shell;

/// <summary>
/// The standalone app's side of <see cref="ICompareHost"/>: its own theme switch (<see cref="AppTheme"/>), its own saved
/// sign-ins (<see cref="ShellSettings.Servers"/>, asked for in <see cref="ConnectWindow"/>), and files open in whatever
/// Windows associates with them. Plain SqlClient connections: there's no SSMS to borrow a token from.
/// </summary>
internal sealed class ShellCompareHost : ICompareHost
{
    private readonly ShellSettings _settings;

    public ShellCompareHost(ShellSettings settings)
    {
        _settings = settings;
        AppTheme.Changed += (s, e) => ThemeChanged?.Invoke(s, e);
    }

    public bool IsDark => AppTheme.IsDark;

    public event EventHandler ThemeChanged;

    public Brush GetBrush(string key) => AppTheme.Get(key);

    public bool HasThemeSwitch => true;

    public string ThemeMode
    {
        get => AppTheme.Mode switch { AppThemeMode.Light => "light", AppThemeMode.Dark => "dark", _ => "ssms" };
        set
        {
            var mode = value == "light" ? AppThemeMode.Light : value == "dark" ? AppThemeMode.Dark : AppThemeMode.MatchSsms;
            if (mode == AppTheme.Mode) return;
            _settings.Theme = mode;
            AppTheme.SetMode(mode);
            _settings.Save();
        }
    }

    public string ThemeSource => AppTheme.Source;

    public CompareSettings Settings => _settings;

    public string ChooseDatabase(Window owner, string current)
    {
        var dialog = new ConnectWindow(_settings, current) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Source : null;
    }

    /// <summary>A SQL login whose password isn't remembered is asked for here.</summary>
    public bool PrepareDatabase(Window owner, string source, string server, out string message)
    {
        message = null;
        if (!_settings.LoginFor(server).NeedsPassword) return true;

        var dialog = new ConnectWindow(_settings, source, $"Enter the password for {server}.") { Owner = owner };
        if (dialog.ShowDialog() == true) return true;
        message = "Compare cancelled: no password given.";
        return false;
    }

    public string ConnectionStringFor(string server, string database) => _settings.LoginFor(server).ConnectionString(database);

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
