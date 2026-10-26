using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SQLExtended.Formatting;
using SQLExtended.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace SQLExtended;

/// <summary>
/// Format With… (issue #8): tick the rules for one run, or pick, save, reorder and delete the presets that
/// Format Preset 1–5 apply. Saving writes the settings straight away, so a preset is there for its shortcut even if the
/// dialog is then closed without applying anything. Apply hands the ticked rules back through <see cref="Chosen"/>.
/// </summary>
public partial class FormatWithWindow : Window
{
    private sealed class Row(FormatPreset preset, int index)
    {
        public FormatPreset Preset { get; } = preset;
        public string Name => Preset.Name;
        public string Slot => index < FormatWithCommand.PresetSlots ? $"Preset {index + 1}" : "";
    }

    private List<FormatPreset> _presets;
    private bool _loading;

    internal FormatWithWindow()
    {
        InitializeComponent();
        _presets = SQLExtendedSettings.Load().GetFormatPresets();
        Refresh(_presets.Count > 0 ? 0 : -1);
        if (_presets.Count == 0)
            ShowPreset(new FormatPreset());
    }

    /// <summary>The rules to apply, once the dialog has returned true.</summary>
    internal FormatPreset Chosen { get; private set; }

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

    private void Refresh(int select)
    {
        _loading = true;
        PresetList.ItemsSource = _presets.Select((p, i) => new Row(p, i)).ToList();
        PresetList.SelectedIndex = Math.Min(select, _presets.Count - 1);
        _loading = false;
        if (PresetList.SelectedItem is Row row)
            ShowPreset(row.Preset);
        UpdateButtons();
    }

    private void ShowPreset(FormatPreset preset)
    {
        _loading = true;
        FullFormatBox.IsChecked = preset.FullFormat;
        KeywordCaseCombo.SelectedIndex = CaseIndex(preset.KeywordCase);
        FunctionCaseCombo.SelectedIndex = CaseIndex(preset.FunctionCase);
        IndentationCombo.SelectedIndex = (int)preset.Indentation;
        RemoveBracketsBox.IsChecked = preset.RemoveBrackets;
        ShortJoinsBox.IsChecked = preset.ShortJoinKeywords;
        SemicolonsBox.IsChecked = preset.AddSemicolons;
        TrimBox.IsChecked = preset.TrimTrailingWhitespace;
        NameBox.Text = preset.Name;
        _loading = false;
        UpdateButtons();
    }

    // What the controls say now.
    private FormatPreset Current() => new()
    {
        Name = NameBox.Text.Trim(),
        FullFormat = FullFormatBox.IsChecked == true,
        KeywordCase = CaseAt(KeywordCaseCombo.SelectedIndex),
        FunctionCase = CaseAt(FunctionCaseCombo.SelectedIndex),
        Indentation = (IndentConversion)Math.Max(0, IndentationCombo.SelectedIndex),
        RemoveBrackets = RemoveBracketsBox.IsChecked == true,
        ShortJoinKeywords = ShortJoinsBox.IsChecked == true,
        AddSemicolons = SemicolonsBox.IsChecked == true,
        TrimTrailingWhitespace = TrimBox.IsChecked == true,
    };

    // Combo order is Leave / UPPER / lower; the enum's is Upper / Lower / Unchanged.
    private static int CaseIndex(CasingOption c) => c switch { CasingOption.Upper => 1, CasingOption.Lower => 2, _ => 0 };
    private static CasingOption CaseAt(int i) => i switch { 1 => CasingOption.Upper, 2 => CasingOption.Lower, _ => CasingOption.Unchanged };

    private void UpdateButtons()
    {
        if (ApplyButton == null)
            return;
        var current = Current();
        ApplyButton.IsEnabled = !current.IsEmpty;
        SaveButton.IsEnabled = !current.IsEmpty && current.Name.Length > 0;
        SaveButton.Content = _presets.Any(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase)) ? "Update preset" : "Save preset";
        DeleteButton.IsEnabled = PresetList.SelectedItem != null;
        MoveUpButton.IsEnabled = PresetList.SelectedIndex > 0;
    }

    private void PresetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loading && PresetList.SelectedItem is Row row)
            ShowPreset(row.Preset);
        UpdateButtons();
    }

    private void PresetList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PresetList.SelectedItem is Row)
            Apply_Click(sender, e);
    }

    private void Rule_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            UpdateButtons();
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_loading)
            UpdateButtons();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var current = Current();
        if (current.IsEmpty || current.Name.Length == 0)
            return;

        int index = _presets.FindIndex(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _presets[index] = current;
        else
            _presets.Add(current);
        Persist(index >= 0 ? index : _presets.Count - 1);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        int index = PresetList.SelectedIndex;
        if (index < 0)
            return;
        _presets.RemoveAt(index);
        Persist(Math.Max(0, index - 1));
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int index = PresetList.SelectedIndex;
        if (index <= 0)
            return;
        (_presets[index - 1], _presets[index]) = (_presets[index], _presets[index - 1]);
        Persist(index - 1);
    }

    private void Persist(int select)
    {
        var settings = SQLExtendedSettings.Load();
        settings.FormatPresets = _presets.ConvertAll(p => p.Clone());
        settings.Save();
        Refresh(select);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var current = Current();
        if (current.IsEmpty)
            return;
        Chosen = current;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
