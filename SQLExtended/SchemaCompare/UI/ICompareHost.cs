using System;
using System.Windows;
using System.Windows.Media;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// Everything the compare needs that differs between running inside SSMS (<c>SsmsCompareHost</c>) and the standalone
/// <c>SchemaCompare.exe</c> shell (<c>ShellCompareHost</c>): where the colours come from, how a database side signs
/// in, and where a file opens. The view, the diff and the script window reach it through <see cref="CompareHost.Current"/>.
/// </summary>
public interface ICompareHost
{
    // ---- theme ----

    bool IsDark { get; }

    /// <summary>Raised on the UI thread after the palette was swapped.</summary>
    event EventHandler ThemeChanged;

    /// <summary>The current brush for a palette key, as a value. It won't follow a swap: re-read it on <see cref="ThemeChanged"/>.</summary>
    Brush GetBrush(string key);

    /// <summary>The shell's Light / Dark / Match SSMS switch. Inside SSMS the theme is SSMS's and there is no switch.</summary>
    bool HasThemeSwitch { get; }

    /// <summary>Only asked when <see cref="HasThemeSwitch"/>; "ssms", "light" or "dark".</summary>
    string ThemeMode { get; set; }

    /// <summary>Tooltip for the theme switch: where the colours currently come from.</summary>
    string ThemeSource { get; }

    // ---- settings ----

    CompareSettings Settings { get; }

    // ---- database sides ----

    /// <summary>Lets the user pick a server and database. Returns <c>mssql://server/database</c>, or null if cancelled.</summary>
    string ChooseDatabase(Window owner, string current);

    /// <summary>
    /// Called on the UI thread before a compare for each <c>mssql://</c> side: anything that has to be asked for (a
    /// password, a connection) is asked for here. <paramref name="source"/> is the whole <c>mssql://server/database</c> text. False
    /// cancels the compare; <paramref name="message"/> says why.
    /// </summary>
    bool PrepareDatabase(Window owner, string source, string server, out string message);

    /// <summary>A connection string for <paramref name="database"/> on <paramref name="server"/>. Called on the UI thread, straight after <see cref="PrepareDatabase"/> said yes.</summary>
    string ConnectionStringFor(string server, string database);

    // ---- files ----

    void OpenFile(string path);
}

public static class CompareHost
{
    /// <summary>Set once by whichever host starts first (the package, or the shell's App).</summary>
    public static ICompareHost Current { get; set; }
}
