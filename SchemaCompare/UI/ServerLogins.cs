using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using SQLExtended.SchemaCompare.UI;

namespace SQLExtended.SchemaCompare.Shell;

/// <summary>How a saved server signs in.</summary>
public enum DbAuthentication
{
    Windows,
    SqlServer,
    EntraMfa,
    EntraIntegrated,
}

/// <summary>A server the user has connected to: how to sign in, never which database (that is in the source text).</summary>
public sealed class ServerLogin
{
    private string _password;

    public string Server { get; set; }
    public DbAuthentication Authentication { get; set; } = DbAuthentication.Windows;
    public string User { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    public bool RememberPassword { get; set; }

    /// <summary>DPAPI (current Windows user), base64. Only written when <see cref="RememberPassword"/> is on.</summary>
    public string ProtectedPassword { get; set; }

    /// <summary>The password for this session. Never serialised in clear; see <see cref="ProtectedPassword"/>.</summary>
    [JsonIgnore]
    public string Password
    {
        get
        {
            if (_password == null && !string.IsNullOrEmpty(ProtectedPassword))
            {
                try
                {
                    _password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser));
                }
                catch
                {
                    ProtectedPassword = null; // Another user's or machine's blob: ask again.
                }
            }

            return _password;
        }
        set
        {
            _password = value;
            ProtectedPassword = RememberPassword && !string.IsNullOrEmpty(value)
                ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
    }

    /// <summary>A SQL login whose password isn't known this session has to be asked for before connecting.</summary>
    [JsonIgnore]
    public bool NeedsPassword => Authentication == DbAuthentication.SqlServer && string.IsNullOrEmpty(Password);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SQLExtended.SchemaCompare");

    public string ConnectionString(string database)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = string.IsNullOrEmpty(database) ? "master" : database,
            TrustServerCertificate = TrustServerCertificate,
            ApplicationName = "SQLExtended Schema Compare",
            ConnectTimeout = 15,
        };
        switch (Authentication)
        {
            case DbAuthentication.SqlServer:
                b.UserID = User ?? "";
                b.Password = Password ?? "";
                break;
            case DbAuthentication.EntraMfa:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(User)) b.UserID = User;
                break;
            case DbAuthentication.EntraIntegrated:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                break;
            default:
                b.IntegratedSecurity = true;
                break;
        }

        return b.ConnectionString;
    }

    public ServerLogin Clone() => new()
    {
        Server = Server, Authentication = Authentication, User = User, TrustServerCertificate = TrustServerCertificate,
        RememberPassword = RememberPassword, ProtectedPassword = ProtectedPassword, _password = _password,
    };
}

/// <summary>
/// The shell's settings: the shared ones, plus its theme and the per-server sign-ins. Inside SSMS neither exists (the
/// theme is SSMS's, the connections are SSMS's), and <see cref="CompareSettings.Extra"/> carries them through a save there.
/// </summary>
public sealed class ShellSettings : CompareSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.MatchSsms;

    /// <summary>How to sign in to each server chosen as a database source, most recent first.</summary>
    public List<ServerLogin> Servers { get; set; } = new();

    /// <summary>The saved sign-in for <paramref name="server"/>, or Windows authentication when there is none.</summary>
    public ServerLogin LoginFor(string server)
        => Servers.FirstOrDefault(s => string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase)) ?? new ServerLogin { Server = server };

    public void Remember(ServerLogin login)
    {
        Servers.RemoveAll(s => string.Equals(s.Server, login.Server, StringComparison.OrdinalIgnoreCase));
        Servers.Insert(0, login);
        if (Servers.Count > 20) Servers.RemoveRange(20, Servers.Count - 20);
    }

    /// <summary>The user databases the login can open, by name. Blocking: call from a background thread.</summary>
    public static List<string> ListDatabases(ServerLogin login)
    {
        const string sql = @"SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;";
        using var connection = new SqlConnection(login.ConnectionString(null));
        connection.Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
