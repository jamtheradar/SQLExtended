using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using SQLExtended.Cache;
using SQLExtended.SchemaCompare.UI;
using SQLExtended.Theme;

namespace SQLExtended.SchemaCompare.Ssms;

/// <summary>
/// The extension's side of <see cref="ICompareHost"/>. Colours are SSMS's (<see cref="ThemeManager"/>, no switch), and a
/// database side signs in with a connection SSMS already has — the active query window's, or one the schema cache has
/// seen this session — so there is no login dialog and nothing saved. Files open in an SSMS editor tab.
/// </summary>
internal sealed class SsmsCompareHost : ICompareHost
{
    private static SsmsCompareHost _instance;

    /// <summary>Server (as written in the source box) → a connection string SSMS harvested for it, credential-free.</summary>
    private readonly Dictionary<string, string> _connections = new(StringComparer.OrdinalIgnoreCase);

    private SsmsCompareHost()
    {
        Settings = CompareSettings.Load<CompareSettings>();
        ThemeManager.Changed += (s, e) => ThemeChanged?.Invoke(s, e);
    }

    /// <summary>Installs the host on first use. UI thread.</summary>
    public static void EnsureInstalled()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_instance != null) return;
        _instance = new SsmsCompareHost();
        CompareHost.Current = _instance;
    }

    public bool IsDark => ThemeManager.IsDark;

    public event EventHandler ThemeChanged;

    public Brush GetBrush(string key) => ThemeManager.Get(key);

    public bool HasThemeSwitch => false;

    public string ThemeMode { get => "ssms"; set { } }

    public string ThemeSource => "SSMS";

    public CompareSettings Settings { get; }

    public string ChooseDatabase(Window owner, string current)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var servers = KnownServers();
        DatabaseSide.TryParse(current, out string currentServer, out string currentDatabase);
        var dialog = new DatabasePickerDialog(servers, currentServer, currentDatabase) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        _connections[dialog.Server] = servers[dialog.Server];
        return DatabaseSide.Format(dialog.Server, dialog.Database);
    }

    /// <summary>A database side needs a connection SSMS already has to that server: there is no login dialog in here.</summary>
    public bool PrepareDatabase(Window owner, string source, string server, out string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        message = null;
        if (_connections.ContainsKey(server)) return true;

        // A source from the history: find this session's connection to the same server.
        if (KnownServers().TryGetValue(server, out string connection))
        {
            _connections[server] = connection;
            return true;
        }

        message = $"No connection to {server} in SSMS yet. Connect a query window (or Object Explorer) to it, then compare again.";
        return false;
    }

    public string ConnectionStringFor(string server, string database)
        => ConnectionHelper.GetConnectionStringForDatabase(_connections[server], database);

    public void OpenFile(string path) => VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);

    /// <summary>Every server SSMS has a connection to that we can see: the active window's first, then the schema cache's.</summary>
    private static Dictionary<string, string> KnownServers()
    {
        var servers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString)) return;
            try
            {
                string server = ConnectionHelper.NormalizeHarvestedDataSource(new SqlConnectionStringBuilder(connectionString).DataSource);
                if (!string.IsNullOrEmpty(server) && server.IndexOf('/') < 0 && !servers.ContainsKey(server)) servers[server] = connectionString;
            }
            catch
            {
                // Not a connection string we can read: not a server we can offer.
            }
        }

        try { Add(ConnectionHelper.GetActiveConnectionString()); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Warning("SchemaCompare", "Reading the active connection failed", ex); }

        try
        {
            foreach (var (_, connectionString) in SchemaCache.Instance.GetKnownConnectionKeys()) Add(connectionString);
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("SchemaCompare", "Reading the schema cache's connections failed", ex);
        }

        return servers;
    }
}
