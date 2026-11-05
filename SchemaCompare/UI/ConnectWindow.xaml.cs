using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Shell;

/// <summary>
/// Picks a server, how to sign in to it, and one of its databases. The result is a <c>mssql://server/database</c>
/// source; the sign-in is saved per server in <see cref="ShellSettings.Servers"/>.
/// </summary>
public partial class ConnectWindow : Window
{
    private readonly ShellSettings _settings;
    private string _listedFor, _shownServer;
    private bool _loading;

    public ConnectWindow(ShellSettings settings, string currentSource, string message = null)
    {
        _settings = settings;
        InitializeComponent();
        WindowTitleBar.Follow(this);

        ServerBox.ItemsSource = _settings.Servers.Select(s => s.Server).ToList();
        if (DatabaseSide.TryParse(currentSource, out string server, out string database))
        {
            ShowLogin(_settings.LoginFor(server));
            DatabaseBox.Text = database;
        }
        else
        {
            ShowLogin(_settings.Servers.FirstOrDefault() ?? new ServerLogin { Server = "localhost" });
        }

        if (message != null) StatusText.Text = message;
        Loaded += (_, _) =>
        {
            if (PasswordPanel.Visibility == Visibility.Visible && PasswordBox.Password.Length == 0 && UserBox.Text.Length > 0) PasswordBox.Focus();
            else ServerBox.Focus();
        };
    }

    /// <summary>The chosen <c>mssql://server/database</c>.</summary>
    public string Source { get; private set; }

    private DbAuthentication SelectedAuth => (DbAuthentication)Math.Max(AuthBox.SelectedIndex, 0);

    private void ShowLogin(ServerLogin login)
    {
        _shownServer = login.Server ?? "";
        ServerBox.Text = _shownServer;
        AuthBox.SelectedIndex = (int)login.Authentication;
        UserBox.Text = login.User ?? "";
        RememberPassword.IsChecked = login.RememberPassword;
        PasswordBox.Password = login.Password ?? "";
        TrustCertificate.IsChecked = login.TrustServerCertificate;
        ApplyAuth();
    }

    /// <summary>The login as the form stands. A fresh object, so a cancelled dialog changes nothing saved.</summary>
    private ServerLogin ReadLogin()
    {
        var login = new ServerLogin
        {
            Server = ServerBox.Text.Trim(),
            Authentication = SelectedAuth,
            TrustServerCertificate = TrustCertificate.IsChecked == true,
        };
        if (login.Authentication is DbAuthentication.SqlServer or DbAuthentication.EntraMfa) login.User = UserBox.Text.Trim();
        if (login.Authentication == DbAuthentication.SqlServer)
        {
            login.RememberPassword = RememberPassword.IsChecked == true; // Before Password: the setter decides whether to keep it.
            login.Password = PasswordBox.Password;
        }

        return login;
    }

    private void ApplyAuth()
    {
        var auth = SelectedAuth;
        UserPanel.Visibility = auth is DbAuthentication.SqlServer or DbAuthentication.EntraMfa ? Visibility.Visible : Visibility.Collapsed;
        UserLabel.Text = auth == DbAuthentication.EntraMfa ? "USER NAME (OPTIONAL)" : "LOGIN";
        PasswordPanel.Visibility = auth == DbAuthentication.SqlServer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ApplyAuth();
    }

    private void OnServerChosen(object sender, SelectionChangedEventArgs e)
    {
        if (ServerBox.SelectedItem is string server) Dispatcher.BeginInvoke(new Action(() => ShowLogin(_settings.LoginFor(server))));
    }

    /// <summary>Typing a server already saved brings back its sign-in, as picking it from the list does.</summary>
    private void OnServerTextChanged(object sender, RoutedEventArgs e)
    {
        string server = ServerBox.Text.Trim();
        if (string.Equals(server, _shownServer, StringComparison.OrdinalIgnoreCase)) return;
        _shownServer = server;
        var saved = _settings.Servers.FirstOrDefault(s => string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase));
        if (saved != null) ShowLogin(saved);
    }

    private async void OnDatabaseDropDown(object sender, EventArgs e)
    {
        if (_listedFor != ReadLogin().ConnectionString(null)) await ListDatabasesAsync();
    }

    private async void OnListDatabases(object sender, RoutedEventArgs e) => await ListDatabasesAsync();

    private async Task ListDatabasesAsync()
    {
        if (_loading) return;
        var login = ReadLogin();
        if (string.IsNullOrEmpty(login.Server))
        {
            StatusText.Text = "Enter a server name first.";
            return;
        }

        _loading = true;
        OkButton.IsEnabled = false;
        StatusText.Text = $"Connecting to {login.Server}…";
        try
        {
            string typed = DatabaseBox.Text;
            var names = await Task.Run(() => ShellSettings.ListDatabases(login));
            DatabaseBox.ItemsSource = names;
            DatabaseBox.Text = typed;
            _listedFor = login.ConnectionString(null);
            StatusText.Text = names.Count == 0 ? "Connected, but there are no user databases this login can open." : $"{names.Count} database{(names.Count == 1 ? "" : "s")} on {login.Server}.";
        }
        catch (Exception ex)
        {
            DatabaseBox.ItemsSource = null;
            _listedFor = null;
            StatusText.Text = "Couldn't connect: " + ex.Message;
        }
        finally
        {
            _loading = false;
            OkButton.IsEnabled = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var login = ReadLogin();
        string database = DatabaseBox.Text.Trim();
        if (string.IsNullOrEmpty(login.Server) || login.Server.IndexOf('/') >= 0)
        {
            StatusText.Text = "Enter a server name.";
            return;
        }

        if (string.IsNullOrEmpty(database))
        {
            StatusText.Text = "Choose a database.";
            return;
        }

        if (login.Authentication == DbAuthentication.SqlServer && string.IsNullOrEmpty(login.User))
        {
            StatusText.Text = "Enter the login name.";
            return;
        }

        _settings.Remember(login);
        _settings.Save();
        Source = DatabaseSide.Format(login.Server, database);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
