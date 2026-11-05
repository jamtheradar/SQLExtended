using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SQLExtended.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SQLExtended.Theme;

/// <summary>
/// Puts the extension's palette into the application's resources, built from the theme picked in SSMS, and
/// rebuilds it whenever that theme changes.
///
/// <para><b>Where the colours come from.</b> Chrome roles — surfaces, text, borders, selection, buttons, headers —
/// are read from the selected theme through <see cref="VSColorTheme.GetThemedColor"/> (<see cref="ShellRoles"/>),
/// so Dark, Light, Blue and any custom theme are matched exactly rather than approximated. Status and syntax roles
/// have no theme equivalent and come from the <see cref="ThemePalette"/> variant for the theme's brightness.
/// <see cref="ThemePalette.Resolve"/> merges the two and falls back pair by pair where a theme's colours would
/// make our text unreadable.</para>
///
/// <para><b>Why application level.</b> Every window, tool window and dialog the extension opens resolves
/// <c>{DynamicResource Sqlx…}</c> by walking up to <see cref="Application.Resources"/>, and so do the things
/// that are not in any visual tree — context menus, combo popups, DataGrid columns — which a dictionary merged
/// per control would miss. The keys are all prefixed <c>Sqlx</c>, so nothing of the shell's is shadowed.</para>
///
/// <para><b>Why one swap.</b> A change to application resources re-resolves every dynamic reference in every
/// window of the shell. Replacing the single merged dictionary costs one of those walks; rewriting fifty brushes
/// one key at a time would cost fifty.</para>
/// </summary>
internal static class ThemeManager
{
    private static ResourceDictionary _installed;
    private static Dictionary<string, string> _installedHex;
    private static bool _subscribed;

    /// <summary>
    /// Palette role → the theme colour it is read from. The keys are the ones VS's own dialogs paint with, which
    /// is what makes the extension's windows look native beside them. A role not listed here is never read.
    /// </summary>
    private static readonly (string Role, ThemeResourceKey Key)[] ShellRoles =
    [
        ("SqlxSurface",        EnvironmentColors.ToolWindowBackgroundColorKey),
        ("SqlxText",           EnvironmentColors.ToolWindowTextColorKey),
        ("SqlxTextChrome",     EnvironmentColors.ToolWindowTextColorKey),
        ("SqlxBorder",         EnvironmentColors.ToolWindowBorderColorKey),
        ("SqlxAccent",         EnvironmentColors.ToolWindowTabSelectedTabColorKey),
        ("SqlxSurfaceRaised",  HeaderColors.DefaultColorKey),
        ("SqlxDivider",        HeaderColors.SeparatorLineColorKey),
        ("SqlxControl",        CommonControlsColors.ButtonColorKey),
        ("SqlxControlHover",   CommonControlsColors.ButtonHoverColorKey),
        ("SqlxControlPressed", CommonControlsColors.ButtonPressedColorKey),
        ("SqlxTextDisabled",   CommonControlsColors.ButtonDisabledTextColorKey),
        ("SqlxBorderStrong",   CommonControlsColors.TextBoxBorderColorKey),
        ("SqlxSelection",      TreeViewColors.SelectedItemActiveColorKey),
        ("SqlxSelectionText",  TreeViewColors.SelectedItemActiveTextColorKey),
    ];

    /// <summary>True when the dark variant is in force. Dark is the SSMS default, so it is also the answer before initialization.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised on the UI thread after the palette has been swapped, for what cannot follow a resource reference (the AvalonEdit highlighting).</summary>
    public static event EventHandler Changed;

    /// <summary>Installs the palette and starts following theme switches. Call once from package initialization, on the UI thread.</summary>
    public static void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!_subscribed)
        {
            VSColorTheme.ThemeChanged += _ => Apply();
            _subscribed = true;
        }

        Apply();
    }

    /// <summary>
    /// The current brush for <paramref name="key"/>, for converters and code that hands out a brush value rather
    /// than a reference. It does not follow a later theme switch; anything long-lived should use
    /// <see cref="FrameworkElement.SetResourceReference"/> instead.
    /// </summary>
    public static Brush Get(string key) => _installed?[key] as Brush ?? Create(ThemePalette.Hex(key, IsDark));

    private static void Apply()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            bool dark = DetectDark();
            var hex = ThemePalette.Resolve(dark, ReadShell());

            // ThemeChanged also fires for changes that leave every colour we use alone; skip the shell-wide re-resolve then.
            if (_installed != null && _installedHex != null && hex.Count == _installedHex.Count && hex.All(p => _installedHex.TryGetValue(p.Key, out var v) && v == p.Value))
                return;

            var dictionary = Build(hex);
            var merged = Application.Current?.Resources.MergedDictionaries;
            if (merged == null)
            {
                SQLExtendedLog.Error("Theme", "No application resources to install the palette into; the extension's windows will be uncoloured.", null);
                return;
            }

            int index = _installed == null ? -1 : merged.IndexOf(_installed);
            if (index >= 0)
                merged[index] = dictionary;
            else
                merged.Add(dictionary);

            _installed = dictionary;
            _installedHex = hex;
            IsDark = dark;

            SQLExtendedLog.Info("Theme", $"Installed the palette from the {(dark ? "dark" : "light")} SSMS theme (surface {hex["SqlxSurface"]}, text {hex["SqlxText"]}).");
            Changed?.Invoke(null, EventArgs.Empty);
            WriteSnapshot(dark, hex);
        }
        catch (Exception ex)
        {
            SQLExtendedLog.Error("Theme", "Could not install the colour palette.", ex);
        }
    }

    /// <summary>
    /// Leaves the palette where SchemaCompare (outside SSMS) can follow it. Only reached when a colour changed, so it is
    /// one small write per theme switch. A failure costs SchemaCompare its match, never the theme here.
    /// </summary>
    private static void WriteSnapshot(bool dark, Dictionary<string, string> hex)
    {
        try
        {
            ThemeSnapshot.Write(ThemeSnapshot.DefaultPath, dark, hex);
        }
        catch (Exception ex)
        {
            SQLExtendedLog.Warning("Theme", $"Could not write {ThemeSnapshot.DefaultPath} for SchemaCompare.", ex);
        }
    }

    private static ResourceDictionary Build(Dictionary<string, string> hex)
    {
        var dictionary = new ResourceDictionary();
        foreach (var pair in hex)
            dictionary[pair.Key] = Create(pair.Value);

        return dictionary;
    }

    /// <summary>
    /// The theme's colour for each of <see cref="ShellRoles"/>, as hex. A key the theme leaves transparent or
    /// cannot supply is simply absent, so that role keeps the built-in value — one bad key never costs the rest.
    /// </summary>
    private static Dictionary<string, string> ReadShell()
    {
        var shell = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, key) in ShellRoles)
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(key);
                if (c.A == 0xFF)
                    shell[role] = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch (Exception ex)
            {
                SQLExtendedLog.Warning("Theme", $"The theme has no colour for {role}; using the built-in one.", ex);
            }
        }

        return shell;
    }

    private static SolidColorBrush Create(string hex)
    {
        var (a, r, g, b) = ThemePalette.Parse(hex);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();

        return brush;
    }

    /// <summary>Measured from the tool-window background rather than matched on theme names, so a third-party theme still gets the right variant.</summary>
    private static bool DetectDark()
    {
        try
        {
            var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            return ThemePalette.IsDarkBackground(background.R, background.G, background.B);
        }
        catch (Exception ex)
        {
            SQLExtendedLog.Error("Theme", "Could not read the shell theme; assuming dark.", ex);
            return true;
        }
    }
}
