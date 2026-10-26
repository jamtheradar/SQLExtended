using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SQLExtended.Search;
using SQLExtended.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SQLExtended;

/// <summary>
/// Dialog that displays the schema script with Copy and Refresh buttons.
/// For tables, a Temp Table tab shows a CREATE TABLE #name version of the script.
/// Press Escape to close, Ctrl+A to select all, Ctrl+C to copy.
/// </summary>
public partial class SchemaDialog : Window
{
    private readonly string _objectName;
    private string _connectionString;
    private readonly string _highlightTerm;

    public SchemaDialog(string objectName, string schemaScript, string connectionString = null, string highlightTerm = null)
    {
        InitializeComponent();

        _objectName = objectName;
        _connectionString = connectionString;
        _highlightTerm = highlightTerm;

        ObjectNameHeader.Text = objectName;
        Title = $"Schema: {objectName}";

        RestoreSize();

        InitializeSyntaxHighlighting();
        ApplyScript(schemaScript);

        Loaded += (s, e) =>
        {
            SchemaTextBox.Focus();
            HighlightTerm(_highlightTerm);
        };
        Closing += (s, e) => SaveSize();
    }

    /// <summary>
    /// Gives the dialog the SSMS main window as its owner if the caller did not supply one.
    ///
    /// Without an owner a modal WPF window shown from the shell can be placed *behind* the main window. It
    /// is still modal, so SSMS stops responding to input and there is nothing on screen to say why — which
    /// is indistinguishable from a hang, and is what it gets reported as. Most callers cannot supply an
    /// owner themselves: <c>Window.GetWindow</c> returns null for a WPF control hosted in a VS tool window,
    /// and a command handler has no WPF window at all.
    ///
    /// Done at <c>SourceInitialized</c> rather than in the constructor so any owner the caller did set has
    /// already been applied and is not overwritten.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            if (Owner != null) return;
            if (!ThreadHelper.CheckAccess()) return;
            if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell) return;
            if (shell.GetDialogOwnerHwnd(out IntPtr ownerHwnd) != 0 || ownerHwnd == IntPtr.Zero) return;

            new WindowInteropHelper(this).Owner = ownerHwnd;
        }
        catch { /* an unowned dialog is still usable; it just may not stay in front */ }
    }

    private void RestoreSize()
    {
        var settings = SQLExtendedSettings.Load();
        if (settings.SchemaDialogWidth >= MinWidth) Width = settings.SchemaDialogWidth;
        if (settings.SchemaDialogHeight >= MinHeight) Height = settings.SchemaDialogHeight;
    }

    private void SaveSize()
    {
        try
        {
            var settings = SQLExtendedSettings.Load();
            settings.SchemaDialogWidth = Width;
            settings.SchemaDialogHeight = Height;
            settings.Save();
        }
        catch { }
    }

    /// <summary>Selects and scrolls to the first occurrence of <paramref name="term"/> in the schema script.</summary>
    private void HighlightTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return;

        string text = SchemaTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        SchemaTextBox.Select(idx, term.Length);
        var loc = SchemaTextBox.Document.GetLocation(idx);
        SchemaTextBox.ScrollTo(loc.Line, loc.Column);
    }

    private void ApplyScript(string schemaScript)
    {
        SchemaTextBox.Text = schemaScript ?? "";

        bool isTable = TempTableScriptBuilder.IsTableScript(schemaScript);
        TempTableTab.Visibility = isTable ? Visibility.Visible : Visibility.Collapsed;

        if (isTable)
        {
            string tableName = TempTableScriptBuilder.ExtractObjectName(_objectName);
            var settings = SQLExtendedSettings.Load();
            TempTableTextBox.Text = TempTableScriptBuilder.Build(schemaScript, tableName, settings.TempTableDropIfExists);
        }
        else
        {
            TempTableTextBox.Text = "";
            if (SchemaTabs.SelectedItem == TempTableTab)
                SchemaTabs.SelectedItem = SchemaTab;
        }
    }

    private void InitializeSyntaxHighlighting()
    {
        // The embedded T-SQL definition, recoloured for a light theme and re-applied on a switch.
        Theme.TsqlHighlighting.Attach(SchemaTextBox, TempTableTextBox);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(_connectionString))
        {
            try
            {
                _connectionString = ConnectionHelper.GetActiveConnectionString();
            }
            catch { }
        }

        if (string.IsNullOrEmpty(_connectionString))
        {
            MessageBox.Show("No active connection available for refresh.",
                "Schema Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Off the UI thread: rebuilding the script re-queries the server, and for a module defined WITH
        // ENCRYPTION that means opening a dedicated administrator connection and briefly ALTERing the
        // object. Inline, that is a frozen dialog for however long it takes.
        string connectionString = _connectionString;
        string objectName = _objectName;

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            string newScript = null;
            Exception failure = null;

            try
            {
                SchemaQueryService.ClearCache();
                newScript = await System.Threading.Tasks.Task.Run(() => SchemaQueryService.GetSchemaScript(connectionString, objectName));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (failure != null)
                MessageBox.Show($"Error refreshing schema: {failure.Message}", "Schema Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (!string.IsNullOrEmpty(newScript))
                ApplyScript(newScript);
            else
                MessageBox.Show($"Object '{objectName}' not found.", "Schema Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Hands the active tab's script to a new query window. The dialog closes first: it is modal, and a window opened
    /// under a modal dialog cannot be typed into until the dialog goes.
    /// </summary>
    private void OpenInScriptWindow_Click(object sender, RoutedEventArgs e)
    {
        bool tempTable = SchemaTabs.SelectedItem == TempTableTab;
        string script = tempTable ? TempTableTextBox.Text : SchemaTextBox.Text;
        string connectionString = _connectionString;
        string objectName = _objectName;

        Close();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // The temp-table version is a script of our own, not the object's definition; there is no CREATE to rewrite.
                Definition.OpenDefinitionCommand.OpenScript(script, connectionString, objectName, applyMode: !tempTable);
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Warning("SchemaDialog", "Open in Script Window failed", ex);
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var activeEditor = SchemaTabs.SelectedItem == TempTableTab ? TempTableTextBox : SchemaTextBox;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (activeEditor.SelectionLength == 0)
            {
                CopyToClipboard();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            activeEditor.SelectAll();
            e.Handled = true;
        }
    }

    private void CopyToClipboard()
    {
        var activeEditor = SchemaTabs.SelectedItem == TempTableTab ? TempTableTextBox : SchemaTextBox;

        try
        {
            Clipboard.SetText(activeEditor.Text);

            string originalTitle = Title;
            Title = $"Schema: {_objectName} \u2014 Copied!";

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            timer.Tick += (s, args) =>
            {
                Title = originalTitle;
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy: {ex.Message}",
                "Schema Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
