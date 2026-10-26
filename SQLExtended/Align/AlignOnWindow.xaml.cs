using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace SQLExtended.Align;

/// <summary>
/// The Align On picker: a box for the token and a short list of the usual ones. Typed text wins; Up/Down from the box
/// picks a preset into it. Enter aligns the first occurrence on each line, Shift+Enter every occurrence.
///
/// <para>A transient modal popup like <see cref="ResultsGrid.Columns.GoToColumnWindow"/>, for the same reason given
/// there: shown modelessly from a command, the shell hands the focus straight back to the editor.</para>
/// </summary>
public partial class AlignOnWindow : Window
{
    internal sealed class Preset(string token, string description)
    {
        public string Token { get; } = token;
        public string Description { get; } = description;
    }

    private static readonly Preset[] Presets =
    {
        new("AS", "column and table aliases"),
        new("=", "comparisons and assignments"),
        new("ON", "join conditions"),
        new("--", "trailing comments"),
        new(",", "list separators"),
        new("THEN", "CASE branches"),
    };

    // Remembered for the session: aligning a block on AS, then ON, then = is the usual run, and the last one is a good start.
    private static string _lastToken = "AS";

    private bool _closing;
    private bool _picking;

    internal AlignOnWindow()
    {
        InitializeComponent();
        PresetList.ItemsSource = Presets;
        TokenBox.Text = _lastToken;
        Loaded += (s, e) => { Activate(); TokenBox.Focus(); Keyboard.Focus(TokenBox); TokenBox.SelectAll(); };
    }

    /// <summary>The token to align on, once the dialog has returned true.</summary>
    internal string Token { get; private set; }

    /// <summary>True for Shift+Enter: align every occurrence on each line, not only the first.</summary>
    internal bool EveryOccurrence { get; private set; }

    /// <summary>Owned by the SSMS main window, as the extension's other popups are, so it opens over the shell.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            if (!ThreadHelper.CheckAccess()) return;
            if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell) return;
            if (shell.GetDialogOwnerHwnd(out IntPtr ownerHwnd) != 0 || ownerHwnd == IntPtr.Zero) return;

            new WindowInteropHelper(this).Owner = ownerHwnd;
        }
        catch { /* an unowned window is still usable */ }
    }

    // Typing selects the preset it names, so the list shows what Enter will do.
    private void TokenBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_picking)
            return;
        string text = TokenBox.Text.Trim();
        PresetList.SelectedItem = Presets.FirstOrDefault(p => string.Equals(p.Token, text, StringComparison.OrdinalIgnoreCase));
    }

    private void TokenBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter: Commit(TokenBox.Text, (Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; break;
            case Key.Escape: CloseOnce(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        int index = PresetList.SelectedIndex < 0 ? (delta > 0 ? 0 : Presets.Length - 1) : PresetList.SelectedIndex + delta;
        PresetList.SelectedIndex = Math.Max(0, Math.Min(Presets.Length - 1, index));
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is not Preset preset || string.Equals(TokenBox.Text.Trim(), preset.Token, StringComparison.OrdinalIgnoreCase))
            return;
        _picking = true;
        TokenBox.Text = preset.Token;
        TokenBox.SelectAll();
        _picking = false;
    }

    private void PresetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PresetList.SelectedItem is Preset preset)
            Commit(preset.Token, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    private void Commit(string token, bool everyOccurrence)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || _closing)
            return;
        Token = _lastToken = token;
        EveryOccurrence = everyOccurrence;
        _closing = true;
        DialogResult = true;   // closes the dialog
    }

    private void Window_Deactivated(object sender, EventArgs e) => CloseOnce();

    // Closing raises Deactivated, which would close again a window that is already closing.
    private void CloseOnce()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }
}
