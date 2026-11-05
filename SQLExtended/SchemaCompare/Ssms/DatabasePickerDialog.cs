using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Ssms;

/// <summary>
/// Picks a database side inside SSMS: one of the servers SSMS is connected to, then one of its databases. No sign-in
/// fields — the connection is SSMS's. Built in code; the controls take the compare's own templates (<c>Theme.xaml</c>).
/// </summary>
internal sealed class DatabasePickerDialog : Window
{
    private readonly IReadOnlyDictionary<string, string> _servers;
    private readonly ComboBox _serverBox, _databaseBox;
    private readonly TextBlock _status;
    private readonly Button _ok;
    private int _listing;

    public DatabasePickerDialog(IReadOnlyDictionary<string, string> servers, string currentServer, string currentDatabase)
    {
        _servers = servers;
        Title = "Choose a database — Schema Compare";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 12.5;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "SqlxSurface");
        SetResourceReference(ForegroundProperty, "SqlxText");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SQLExtended;component/SchemaCompare/UI/Theme.xaml", UriKind.Relative) });
        WindowTitleBar.Follow(this);

        _serverBox = new ComboBox { ItemsSource = servers.Keys.ToList(), Margin = new Thickness(0, 4, 0, 12) };
        _databaseBox = new ComboBox { IsEditable = true, Margin = new Thickness(0, 4, 0, 12) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SqlxTextMuted");
        _ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        _ok.Click += OnOk;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Label("SERVER"));
        panel.Children.Add(_serverBox);
        panel.Children.Add(Label("DATABASE"));
        panel.Children.Add(_databaseBox);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        _serverBox.SelectionChanged += async (_, _) => await ListDatabasesAsync(null);

        if (servers.Count == 0)
        {
            _status.Text = "SSMS has no connections this extension can see yet. Connect a query window or Object Explorer to a server, then try again.";
            _ok.IsEnabled = false;
        }
        else
        {
            string start = currentServer != null && servers.ContainsKey(currentServer) ? currentServer : servers.Keys.First();
            Loaded += async (_, _) =>
            {
                _serverBox.SelectedItem = start;
                await ListDatabasesAsync(currentServer == start ? currentDatabase : null);
            };
        }
    }

    public string Server { get; private set; }

    public string Database { get; private set; }

    private TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SqlxTextSubtle");
        return label;
    }

    private async Task ListDatabasesAsync(string select)
    {
        if (_serverBox.SelectedItem is not string server) return;
        int listing = ++_listing; // A newer pick wins over a slow answer for the last one.
        string connection = _servers[server];
        _status.Text = $"Reading the databases on {server}…";
        try
        {
            var names = await Task.Run(() => ListDatabases(connection));
            if (listing != _listing) return;
            _databaseBox.ItemsSource = names;
            _databaseBox.Text = select ?? names.FirstOrDefault() ?? "";
            _status.Text = names.Count == 0 ? "No user databases this login can open." : $"{names.Count} database{(names.Count == 1 ? "" : "s")} on {server}.";
        }
        catch (Exception ex)
        {
            if (listing != _listing) return;
            _databaseBox.ItemsSource = null;
            _status.Text = "Couldn't read the databases: " + ex.Message;
        }
    }

    private static List<string> ListDatabases(string connectionString)
    {
        const string sql = "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;";
        using var connection = SqlConnectionFactory.Create(ConnectionHelper.GetConnectionStringForDatabase(connectionString, "master"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string database = _databaseBox.Text?.Trim();
        if (_serverBox.SelectedItem is not string server)
        {
            _status.Text = "Choose a server.";
            return;
        }

        if (string.IsNullOrEmpty(database))
        {
            _status.Text = "Choose a database.";
            return;
        }

        Server = server;
        Database = database;
        DialogResult = true;
    }
}
