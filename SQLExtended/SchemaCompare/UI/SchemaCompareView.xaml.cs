using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SQLExtended.SchemaCompare.Engine;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// The whole compare: sources, object list, options and diff. Hosted by the SSMS tool window and by the standalone
/// shell's MainWindow; what differs between the two is behind <see cref="ICompareHost"/>.
/// </summary>
public partial class SchemaCompareView : UserControl
{
    private readonly ICompareHost _host;
    private readonly CompareSettings _settings;
    private readonly DispatcherTimer _optionsDebounce;
    private SchemaSet _left, _right;
    private List<CompareItem> _items = new();
    private bool _busy;

    public SchemaCompareView()
    {
        _host = CompareHost.Current ?? throw new InvalidOperationException("CompareHost.Current must be set before the view is created.");
        _settings = _host.Settings;
        InitializeComponent();

        if (_host.HasThemeSwitch)
        {
            (_host.ThemeMode switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSsms }).IsChecked = true;
            ThemeSwitch.ToolTip = _host.ThemeSource;
        }
        else
        {
            ThemeSwitch.Visibility = Visibility.Collapsed; // Inside SSMS the theme is SSMS's.
        }

        // Brushes handed out as values (the list's status glyphs, the chip) don't follow a resource swap; re-issue them.
        // Held only while loaded: the host's event outlives a closed tool window.
        EventHandler themeChanged = (_, _) =>
        {
            if (_host.HasThemeSwitch) ThemeSwitch.ToolTip = _host.ThemeSource;
            RefreshList((ObjectList.SelectedItem as ItemView)?.Key);
        };
        Loaded += (_, _) =>
        {
            _host.ThemeChanged -= themeChanged;
            _host.ThemeChanged += themeChanged;
        };
        Unloaded += (_, _) =>
        {
            _host.ThemeChanged -= themeChanged;
            SaveState();
        };

        OptionsHost.DataContext = _settings.Options;
        ShowIdentical.IsChecked = _settings.ShowIdentical;
        OriginalToggle.IsChecked = _settings.ShowOriginal;
        OptionsToggle.IsChecked = _settings.OptionsPanelOpen;
        ApplyOptionsPanel();
        LeftPath.ItemsSource = _settings.LeftHistory.ToList();
        RightPath.ItemsSource = _settings.RightHistory.ToList();
        LeftPath.Text = _settings.LeftHistory.FirstOrDefault() ?? "";
        RightPath.Text = _settings.RightHistory.FirstOrDefault() ?? "";

        // Checkbox clicks arrive in a burst when the user is working down the list; recompare once they stop.
        _optionsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _optionsDebounce.Tick += async (_, _) =>
        {
            _optionsDebounce.Stop();
            await RecompareAsync();
        };

        Diff.HunkChanged += (_, _) => UpdateHunkText();
        SetStatus("Choose two sources and press Compare. Each can be an export folder, a database project (.sqlproj or its folder), any folder of .sql files, or a SQL Server database.");
    }

    public void SetLeft(string path) => LeftPath.Text = path;

    public void StartCompare(string left, string right)
    {
        LeftPath.Text = left;
        RightPath.Text = right;
        _ = CompareAsync();
    }

    private Window OwnerWindow => Window.GetWindow(this);

    /// <summary>Called on unload, and by the shell when its window closes.</summary>
    public void SaveState()
    {
        _settings.ShowIdentical = ShowIdentical.IsChecked == true;
        _settings.ShowOriginal = OriginalToggle.IsChecked == true;
        _settings.OptionsPanelOpen = OptionsToggle.IsChecked == true;
        _settings.Save();
    }

    // ---- loading and comparing ------------------------------------------------------------------------------

    private async void OnCompare(object sender, RoutedEventArgs e) => await CompareAsync();

    private async Task CompareAsync()
    {
        if (_busy) return;
        string leftPath = LeftPath.Text?.Trim().Trim('"'), rightPath = RightPath.Text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath))
        {
            SetStatus("Choose a left and a right source first.");
            return;
        }

        // Anything a database side needs asked for (a password, a connection) is asked for here, on the UI thread,
        // before anything starts. The connection strings are taken here too, so the background work never asks the host.
        if (!PrepareSide(leftPath, out string leftConnection) || !PrepareSide(rightPath, out string rightConnection)) return;

        SetBusy(true);
        try
        {
            void Progress(string message) => Dispatcher.BeginInvoke(new Action(() => SetStatus(message)));
            var started = Stopwatch.StartNew();
            var leftTask = LoadSource(leftPath, leftConnection, "Left", Progress);
            var rightTask = LoadSource(rightPath, rightConnection, "Right", Progress);
            _left = await leftTask;
            _right = await rightTask;

            CompareSettings.Remember(_settings.LeftHistory, leftPath);
            CompareSettings.Remember(_settings.RightHistory, rightPath);
            LeftPath.ItemsSource = _settings.LeftHistory.ToList();
            RightPath.ItemsSource = _settings.RightHistory.ToList();
            LeftPath.Text = leftPath;
            RightPath.Text = rightPath;
            _settings.Save();

            LeftInfo.Text = Describe(_left);
            RightInfo.Text = Describe(_right);
            LeftInfo.ToolTip = Warnings(_left);
            RightInfo.ToolTip = Warnings(_right);

            await RecompareAsync(fromCompare: true);
            SetStatus($"Compared in {started.Elapsed.TotalSeconds:0.0} s. {Summary()}{WarningSummary()}");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't compare: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>A folder or project is read from disk; a live database is scripted into a cache folder first.</summary>
    private static Task<SchemaSet> LoadSource(string path, string connectionString, string side, Action<string> progress)
    {
        if (connectionString == null) return Task.Run(() => ObjectExtractor.Load(path, progress));
        return Task.Run(() => DatabaseSide.Load(path, connectionString, side, progress));
    }

    /// <summary>
    /// For a <c>mssql://</c> side: lets the host ask for whatever it needs, then takes the connection string. A folder
    /// or project gets a null connection string. False when the compare can't go ahead; the status line says why.
    /// </summary>
    private bool PrepareSide(string path, out string connectionString)
    {
        connectionString = null;
        if (!DatabaseSide.IsDatabase(path)) return true;
        if (!DatabaseSide.TryParse(path, out string server, out string database))
        {
            SetStatus($"'{path}' isn't a database: use {DatabaseSide.Prefix}server/database.");
            return false;
        }

        if (!_host.PrepareDatabase(OwnerWindow, path, server, out string message))
        {
            SetStatus(message ?? "Compare cancelled.");
            return false;
        }

        connectionString = _host.ConnectionStringFor(server, database);
        return true;
    }

    /// <summary>Re-normalises with the current options without re-reading the files.</summary>
    private async Task RecompareAsync(bool fromCompare = false)
    {
        if (_left == null || _right == null) return;

        var options = _settings.Options.Clone();
        var left = _left;
        var right = _right;
        string selectedKey = (ObjectList.SelectedItem as ItemView)?.Item.Left?.Key ?? (ObjectList.SelectedItem as ItemView)?.Item.Right?.Key;

        if (!fromCompare) SetStatus("Applying options…");
        _items = await Task.Run(() => SchemaComparer.Compare(left, right, options));
        ScriptButton.IsEnabled = true;
        RefreshList(selectedKey);
        if (!fromCompare) SetStatus(Summary() + WarningSummary());
    }

    private static string Describe(SchemaSet set)
    {
        string kind = set.Kind switch
        {
            SourceKind.Export => "Export",
            SourceKind.DatabaseProject => "Database project",
            SourceKind.Database => "Database (scripted just now)",
            _ => "Folder",
        };
        string text = $"{kind} · {set.FileCount} files · {set.Objects.Count} objects";
        if (set.Warnings.Count > 0) text += $" · ⚠ {set.Warnings.Count} warning{(set.Warnings.Count == 1 ? "" : "s")} (hover)";
        return text;
    }

    private static string Warnings(SchemaSet set) => set.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, set.Warnings.Take(40)) + (set.Warnings.Count > 40 ? $"{Environment.NewLine}… and {set.Warnings.Count - 40} more" : "");

    private string Summary()
    {
        int Count(CompareStatus s) => _items.Count(i => i.Status == s);
        return $"{Count(CompareStatus.Different)} different, {Count(CompareStatus.LeftOnly)} only on the left, {Count(CompareStatus.RightOnly)} only on the right, {Count(CompareStatus.Identical)} identical.";
    }

    private string WarningSummary()
    {
        int warnings = (_left?.Warnings.Count ?? 0) + (_right?.Warnings.Count ?? 0);
        return warnings == 0 ? "" : $" {warnings} warning{(warnings == 1 ? "" : "s")}: hover the source line under each path.";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CompareButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---- the list -------------------------------------------------------------------------------------------

    private void OnFilterChanged(object sender, RoutedEventArgs e) => RefreshList((ObjectList.SelectedItem as ItemView)?.Key);

    private void RefreshList(string selectedKey)
    {
        int Count(CompareStatus s) => _items.Count(i => i.Status == s);
        CountDifferent.Text = $"Different ({Count(CompareStatus.Different)})";
        CountLeftOnly.Text = $"Left only ({Count(CompareStatus.LeftOnly)})";
        CountRightOnly.Text = $"Right only ({Count(CompareStatus.RightOnly)})";
        CountIdentical.Text = $"Identical ({Count(CompareStatus.Identical)})";

        string search = SearchBox.Text?.Trim() ?? "";
        var visible = _items.Where(i => i.Status switch
            {
                CompareStatus.Different => ShowDifferent.IsChecked == true,
                CompareStatus.LeftOnly => ShowLeftOnly.IsChecked == true,
                CompareStatus.RightOnly => ShowRightOnly.IsChecked == true,
                _ => ShowIdentical.IsChecked == true,
            })
            .Where(i => search.Length == 0 || i.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || i.Type.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(i => new ItemView(i)).ToList();

        ObjectList.ItemsSource = visible;
        var reselect = selectedKey == null ? null : visible.FirstOrDefault(v => v.Key == selectedKey);
        if (reselect != null)
        {
            ObjectList.SelectedItem = reselect;
            ObjectList.ScrollIntoView(reselect);
            ShowSelected();
        }
        else if (visible.Count > 0)
        {
            ObjectList.SelectedIndex = 0;
        }
        else
        {
            ShowSelected();
        }
    }

    private void OnObjectSelected(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        if (ObjectList.SelectedItem is not ItemView view)
        {
            ObjectTitle.Text = ObjectType.Text = "";
            StatusChip.Visibility = Visibility.Collapsed;
            LeftFiles.Text = RightFiles.Text = "";
            ScriptObjectButton.IsEnabled = false;
            Diff.Clear();
            return;
        }

        var item = view.Item;
        ScriptObjectButton.IsEnabled = item.Status != CompareStatus.Identical;
        ObjectType.Text = item.Type.ToString();
        ObjectTitle.Text = item.DisplayName;

        // The chip: the status colour as text on a faint wash of itself, readable in either theme.
        var color = (view.StatusBrush as SolidColorBrush)?.Color ?? Colors.Gray;
        StatusChip.Background = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
        StatusChipText.Foreground = view.StatusBrush;
        StatusChipText.Text = view.StatusText;
        StatusChip.Visibility = Visibility.Visible;
        LeftFiles.Text = Files(item.Left, _left) ?? "(not on the left)";
        RightFiles.Text = Files(item.Right, _right) ?? "(not on the right)";
        LeftFiles.ToolTip = item.Left == null ? null : string.Join(Environment.NewLine, item.Left.Files);
        RightFiles.ToolTip = item.Right == null ? null : string.Join(Environment.NewLine, item.Right.Files);

        var options = _settings.Options;
        if (OriginalToggle.IsChecked == true)
        {
            // As written: only case and whitespace are ignored (they are line-level choices, not rewrites).
            Diff.Show(Normalizer.Raw(item.Left), Normalizer.Raw(item.Right), options.IgnoreCase, options.IgnoreWhitespace);
        }
        else
        {
            Diff.Show(item.LeftText, item.RightText, options.IgnoreCase, options.IgnoreWhitespace);
        }
    }

    private static string Files(SchemaObject obj, SchemaSet set)
    {
        if (obj == null) return null;
        var files = obj.Statements.Select(s => s.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return files.Count == 1 ? files[0] : $"{files[0]} (+{files.Count - 1} more)";
    }

    private void UpdateHunkText()
    {
        int count = Diff.HunkCount;
        HunkText.Text = Diff.Diff == null ? "" : count == 0 ? "No differences shown" : $"Difference {Math.Max(Diff.CurrentHunk + 1, 1)} of {count}";
    }

    private void OnNextDifference(object sender, RoutedEventArgs e)
    {
        if (Diff.HunkCount > 0 && Diff.CurrentHunk < Diff.HunkCount - 1) Diff.NextHunk();
        else OnNextObject(sender, e); // At the last difference, Next moves on to the next object, as WinMerge's does.
    }

    private void OnPreviousDifference(object sender, RoutedEventArgs e) => Diff.PreviousHunk();

    private void OnNextObject(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedIndex < ObjectList.Items.Count - 1) ObjectList.SelectedIndex++;
        ObjectList.ScrollIntoView(ObjectList.SelectedItem);
    }

    private void OnPreviousObject(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedIndex > 0) ObjectList.SelectedIndex--;
        ObjectList.ScrollIntoView(ObjectList.SelectedItem);
    }

    private void OnViewModeChanged(object sender, RoutedEventArgs e) => ShowSelected();

    // ---- options --------------------------------------------------------------------------------------------

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        _optionsDebounce.Stop();
        _optionsDebounce.Start();
    }

    private void OnOptionTextChanged(object sender, RoutedEventArgs e) => OnOptionChanged(sender, e);

    private async void OnResetOptions(object sender, RoutedEventArgs e)
    {
        _settings.Options = new CompareOptions();
        OptionsHost.DataContext = _settings.Options;
        await RecompareAsync();
    }

    private void OnOptionsToggled(object sender, RoutedEventArgs e) => ApplyOptionsPanel();

    private void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || !_host.HasThemeSwitch) return; // The constructor's initial check, before the view exists.
        string mode = sender == ThemeLight ? "light" : sender == ThemeDark ? "dark" : "ssms";
        if (mode != _host.ThemeMode) _host.ThemeMode = mode;
    }

    private void ApplyOptionsPanel()
    {
        bool open = OptionsToggle.IsChecked == true;
        OptionsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OptionsColumn.Width = open ? new GridLength(310) : new GridLength(0);
    }

    // ---- sources --------------------------------------------------------------------------------------------

    private void OnBrowseLeft(object sender, RoutedEventArgs e) => Browse(LeftPath);

    private void OnBrowseRight(object sender, RoutedEventArgs e) => Browse(RightPath);

    private void Browse(ComboBox target)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose an export folder, a database project folder, or a folder of .sql files", ShowNewFolderButton = false };
        string current = target.Text?.Trim().Trim('"');
        try
        {
            if (!string.IsNullOrEmpty(current) && !DatabaseSide.IsDatabase(current))
            {
                string folder = File.Exists(current) ? Path.GetDirectoryName(current) : current;
                if (Directory.Exists(folder)) dialog.SelectedPath = folder;
            }
        }
        catch
        {
            // A remembered path on a disconnected drive just means no starting point.
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    private void OnDatabaseLeft(object sender, RoutedEventArgs e) => ChooseDatabase(LeftPath);

    private void OnDatabaseRight(object sender, RoutedEventArgs e) => ChooseDatabase(RightPath);

    private void ChooseDatabase(ComboBox target)
    {
        // Starting from the other side's server is the usual case: the same database on another environment is a new server.
        string current = DatabaseSide.IsDatabase(target.Text) ? target.Text : (target == LeftPath ? RightPath : LeftPath).Text;
        string chosen = _host.ChooseDatabase(OwnerWindow, current);
        if (chosen != null) target.Text = chosen;
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        (_left, _right) = (_right, _left);
        (LeftInfo.Text, RightInfo.Text) = (RightInfo.Text, LeftInfo.Text);
        (LeftInfo.ToolTip, RightInfo.ToolTip) = (RightInfo.ToolTip, LeftInfo.ToolTip);
        _ = RecompareAsync();
    }

    // ---- context menu ---------------------------------------------------------------------------------------

    private void OnOpenLeftFile(object sender, RoutedEventArgs e) => OpenFile((ObjectList.SelectedItem as ItemView)?.Item.Left);

    private void OnOpenRightFile(object sender, RoutedEventArgs e) => OpenFile((ObjectList.SelectedItem as ItemView)?.Item.Right);

    private void OpenFile(SchemaObject obj)
    {
        string file = obj?.Files.FirstOrDefault();
        if (file == null || !File.Exists(file)) return;
        try
        {
            _host.OpenFile(file);
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't open {file}: {ex.Message}");
        }
    }

    // ---- change script --------------------------------------------------------------------------------------

    /// <summary>Scripts what the list shows: its status boxes and name filter narrow the script as they narrow the list.</summary>
    private void OnScriptChanges(object sender, RoutedEventArgs e)
    {
        var shown = (ObjectList.ItemsSource as IEnumerable<ItemView>)?.Select(v => v.Item).Where(i => i.Status != CompareStatus.Identical).ToList() ?? new List<CompareItem>();
        int all = _items.Count(i => i.Status != CompareStatus.Identical);
        if (shown.Count == 0)
        {
            SetStatus("No differences in the list to script.");
            return;
        }

        string scope = shown.Count == all ? $"all {all} differences" : $"the {shown.Count} differences shown in the list (of {all})";
        OpenScript(shown, scope);
    }

    private void OnScriptObject(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedItem is not ItemView view) return;
        if (view.Item.Status == CompareStatus.Identical)
        {
            SetStatus($"{view.Item.DisplayName} is identical: nothing to script.");
            return;
        }

        OpenScript(new List<CompareItem> { view.Item }, $"{view.Item.Type} {view.Item.DisplayName} only");
    }

    private void OpenScript(List<CompareItem> items, string scope)
    {
        if (_left == null || _right == null) return;
        var window = new ScriptWindow(items, _left, _right, _settings.Options.Clone(), _settings, LeftPath.Text, RightPath.Text, scope) { Owner = OwnerWindow };
        window.Show();
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedItem is ItemView view) Clipboard.SetText(view.Item.DisplayName);
    }

    private async void OnExcludeObject(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedItem is not ItemView view) return;
        string pattern = view.Item.Type + ":" + view.Item.DisplayName;
        var options = _settings.Options;
        options.ExcludeObjects = string.IsNullOrWhiteSpace(options.ExcludeObjects) ? pattern : options.ExcludeObjects.TrimEnd() + Environment.NewLine + pattern;
        OptionsHost.DataContext = null;
        OptionsHost.DataContext = options;
        await RecompareAsync();
    }

    /// <summary>A row of the object list.</summary>
    private sealed class ItemView
    {
        public ItemView(CompareItem item) => Item = item;

        public CompareItem Item { get; }

        public string Key => (Item.Left ?? Item.Right)?.Key;

        public string Name => Item.DisplayName;

        public string TypeText => Item.Type.ToString();

        public string Glyph => Item.Status switch
        {
            CompareStatus.Different => "≠",
            CompareStatus.LeftOnly => "◀",
            CompareStatus.RightOnly => "▶",
            _ => "=",
        };

        public string StatusText => Item.Status switch
        {
            CompareStatus.Different => "different",
            CompareStatus.LeftOnly => "only on the left",
            CompareStatus.RightOnly => "only on the right",
            _ => "identical",
        };

        public Brush StatusBrush => CompareHost.Current.GetBrush(Item.Status switch
        {
            CompareStatus.Different => "SqlxWarn",
            CompareStatus.LeftOnly => "SqlxBad",
            CompareStatus.RightOnly => "SqlxGood",
            _ => "SqlxTextMuted",
        });
    }
}
