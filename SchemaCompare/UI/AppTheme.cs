using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SQLExtended.SchemaCompare.UI;
using SQLExtended.Theme;

namespace SQLExtended.SchemaCompare.Shell;

public enum AppThemeMode
{
    /// <summary>The palette the SQLExtended extension last installed in SSMS (<c>theme.ini</c>), else the Windows app theme.</summary>
    MatchSsms,
    Light,
    Dark,
}

/// <summary>
/// The standalone shell's stand-in for the extension's <c>ThemeManager</c>: installs the extension's palette
/// (<see cref="ThemePalette"/>, linked) into the app's resources, so every window here paints with the same <c>Sqlx…</c>
/// roles as inside SSMS.
///
/// <para>"Match SSMS" reads <see cref="ThemeSnapshot"/>, which the extension rewrites on every theme change, and watches it,
/// so switching theme in SSMS switches this window too. Without the extension it follows the Windows app theme.</para>
/// </summary>
internal static class AppTheme
{
    private static ResourceDictionary _installed;
    private static FileSystemWatcher _watcher;
    private static DispatcherTimer _debounce;

    public static AppThemeMode Mode { get; private set; } = AppThemeMode.MatchSsms;

    public static bool IsDark { get; private set; }

    /// <summary>Where the colours came from, for the theme switch's tooltip.</summary>
    public static string Source { get; private set; } = "";

    /// <summary>For what can't follow a resource reference: the AvalonEdit highlighting, the list's status brushes.</summary>
    public static event EventHandler Changed;

    public static void Initialize(AppThemeMode mode)
    {
        Mode = mode;
        Apply();

        // Follow SSMS: the extension replaces theme.ini on every switch.
        try
        {
            Directory.CreateDirectory(ThemeSnapshot.DefaultDirectory);
            _watcher = new FileSystemWatcher(ThemeSnapshot.DefaultDirectory, ThemeSnapshot.FileName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler changed = (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Reapply));
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Reapply));
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // No live follow; the theme is still read at start-up.
        }

        // And Windows, for the fallback.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) Application.Current?.Dispatcher.BeginInvoke(new Action(Reapply));
        };
    }

    public static void SetMode(AppThemeMode mode)
    {
        Mode = mode;
        Apply();
    }

    /// <summary>A replace fires several events in a row; apply once they settle.</summary>
    private static void Reapply()
    {
        if (Mode != AppThemeMode.MatchSsms) return;
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) =>
        {
            _debounce.Stop();
            Apply();
        }, Dispatcher.CurrentDispatcher);
        _debounce.Stop();
        _debounce.Start();
    }

    private static void Apply()
    {
        bool dark;
        IReadOnlyDictionary<string, string> shell = new Dictionary<string, string>();

        switch (Mode)
        {
            case AppThemeMode.Dark:
                dark = true;
                Source = "Dark";
                break;
            case AppThemeMode.Light:
                dark = false;
                Source = "Light";
                break;
            default:
                if (ThemeSnapshot.TryRead(ThemeSnapshot.DefaultPath, out dark, out var hex))
                {
                    shell = hex;
                    Source = $"Matching SSMS ({(dark ? "dark" : "light")}), from {ThemeSnapshot.DefaultPath}";
                }
                else
                {
                    dark = WindowsAppsUseDark();
                    Source = $"Matching the Windows app theme ({(dark ? "dark" : "light")}). Open SSMS once with SQLExtended installed to match its theme instead.";
                }

                break;
        }

        var resolved = ThemePalette.Resolve(dark, shell);
        var dictionary = new ResourceDictionary();
        foreach (var pair in resolved) dictionary[pair.Key] = Brush(pair.Value);

        var merged = Application.Current.Resources.MergedDictionaries;
        int index = _installed == null ? -1 : merged.IndexOf(_installed);
        if (index >= 0) merged[index] = dictionary;
        else merged.Insert(0, dictionary);
        _installed = dictionary;
        IsDark = dark;

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static Brush Get(string key) => _installed?[key] as Brush ?? Brushes.Transparent;

    private static SolidColorBrush Brush(string hex)
    {
        var (a, r, g, b) = ThemePalette.Parse(hex);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static bool WindowsAppsUseDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
