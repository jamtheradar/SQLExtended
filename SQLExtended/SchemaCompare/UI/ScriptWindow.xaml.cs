using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.Win32;
using SQLExtended.SchemaCompare.Engine;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// The change script for what the compare found: which way, whether to drop, whether to wrap it in a transaction.
/// Regenerated on every choice; the text can be edited before it's copied or saved, and a regenerate asks first.
/// </summary>
public partial class ScriptWindow : Window
{
    private readonly List<CompareItem> _items;
    private readonly SchemaSet _left, _right;
    private readonly CompareOptions _compare;
    private readonly CompareSettings _settings;
    private readonly string _leftPath, _rightPath, _scope;
    private readonly bool _ready;
    private bool _edited, _generating;
    private string _generated = "";

    internal ScriptWindow(List<CompareItem> items, SchemaSet left, SchemaSet right, CompareOptions compare, CompareSettings settings, string leftPath, string rightPath, string scope)
    {
        (_items, _left, _right, _compare, _settings, _leftPath, _rightPath, _scope) = (items, left, right, compare, settings, leftPath, rightPath, scope);
        InitializeComponent();

        (_settings.ScriptLeftToRight ? LeftToRight : RightToLeft).IsChecked = true;
        IncludeDrops.IsChecked = _settings.ScriptIncludeDrops;
        UseTransaction.IsChecked = _settings.ScriptUseTransaction;
        LeftToRight.ToolTip = "Script for the right: " + rightPath;
        RightToLeft.ToolTip = "Script for the left: " + leftPath;

        Editor.SetResourceReference(Control.BackgroundProperty, "SqlxSurface");
        Editor.SetResourceReference(Control.ForegroundProperty, "SqlxText");
        Editor.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.LineNumbersForegroundProperty, "SqlxTextMuted");
        Editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "SqlxSelection");
        Editor.TextArea.SetResourceReference(TextArea.SelectionForegroundProperty, "SqlxSelectionText");
        Editor.TextArea.SelectionBorder = null;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = false;
        Editor.TextChanged += (_, _) => _edited = !_generating && Editor.Text != _generated;

        // AvalonEdit's highlighting colours are values, not resource references: swap the definition with the theme.
        var host = CompareHost.Current;
        EventHandler themeChanged = (_, _) => Editor.SyntaxHighlighting = SqlHighlighting.For(host.IsDark);
        themeChanged(null, EventArgs.Empty);
        host.ThemeChanged += themeChanged;
        Closed += (_, _) => host.ThemeChanged -= themeChanged;
        WindowTitleBar.Follow(this);

        _ready = true;
        Loaded += async (_, _) => await RegenerateAsync();
    }

    private async void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ScriptLeftToRight = LeftToRight.IsChecked == true;
        _settings.ScriptIncludeDrops = IncludeDrops.IsChecked == true;
        _settings.ScriptUseTransaction = UseTransaction.IsChecked == true;
        _settings.Save();
        await RegenerateAsync();
    }

    private async Task RegenerateAsync()
    {
        if (_edited && MessageBox.Show(this, "Regenerating replaces the edits you've made to the script. Carry on?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var options = new ScriptOptions
        {
            LeftToRight = LeftToRight.IsChecked == true, IncludeDrops = IncludeDrops.IsChecked == true, UseTransaction = UseTransaction.IsChecked == true,
            LeftName = _leftPath, RightName = _rightPath,
        };
        TargetText.Text = $"Run against the {(options.LeftToRight ? "right" : "left")}: {(options.LeftToRight ? _rightPath : _leftPath)}   ·   {_scope}";
        StatusText.Text = "Writing the script…";
        Cursor = System.Windows.Input.Cursors.AppStarting;

        try
        {
            var compare = _compare;
            var script = await Task.Run(() => ChangeScripter.Generate(_items, _left, _right, compare, options));
            _generating = true;
            _generated = script.Text;
            Editor.Text = script.Text;
            Editor.ScrollToHome();
            _edited = false;

            StatusText.Text = $"{script.Created} to create, {script.Altered} to change, {script.Dropped} to drop"
                + (script.NotDropped.Count > 0 ? $", {script.NotDropped.Count} only on the target and left alone" : "")
                + ". Review it before running it.";
            WarningText.Text = script.Warnings.Count == 0 ? "" : $"⚠ {script.Warnings.Count} warning{(script.Warnings.Count == 1 ? "" : "s")}: see the top of the script";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't write the script: " + ex.Message;
        }
        finally
        {
            _generating = false;
            Cursor = null;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Editor.Text);
        StatusText.Text = "Copied to the clipboard.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string target = LeftToRight.IsChecked == true ? _rightPath : _leftPath;
        string name = Path.GetFileName((target ?? "").TrimEnd('\\', '/'));
        var dialog = new SaveFileDialog
        {
            Title = "Save the change script",
            Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*",
            FileName = $"Change script{(string.IsNullOrEmpty(name) ? "" : " for " + name)} {DateTime.Now:yyyy-MM-dd HHmm}.sql",
        };
        if (!string.IsNullOrEmpty(_settings.ScriptFolder) && Directory.Exists(_settings.ScriptFolder)) dialog.InitialDirectory = _settings.ScriptFolder;
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // With a BOM, so SSMS reads anything outside ASCII in the script as it was written.
            File.WriteAllText(dialog.FileName, Editor.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _settings.ScriptFolder = Path.GetDirectoryName(dialog.FileName);
            _settings.Save();
            StatusText.Text = "Saved " + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't save: " + ex.Message;
        }
    }
}
