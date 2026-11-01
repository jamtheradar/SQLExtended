using Microsoft.Data.SqlClient;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace SQLExtended;

/// <summary>
/// Turns SSMS's <c>UIConnectionInfo</c> into a connection string this extension can reconnect from.
///
/// <para>There were three copies of this - in <see cref="ConnectionHelper"/>, <see cref="ServiceCacheProxy"/> and
/// <see cref="ObjectExplorerHelper"/> - and all three had the same two faults, so a connection was harvested
/// differently depending on which one answered first. They now share this.</para>
///
/// <para><b>AuthenticationType is an <c>int</c>, not an enum</b> (verified against SSMS 22's
/// <c>Microsoft.SqlServer.RegSvrEnum.dll</c>). Every copy tested <c>value.ToString().Contains("Sql")</c>, which is
/// never true of "0" or "1", so the SQL-auth branch was dead code and everything fell through to integrated
/// security. The numbers themselves are undocumented and have shifted between SSMS versions, so as far as possible
/// nothing here reads them: what the connection actually carries - a renewable token, a password - is what decides.
/// Entra MFA is the exception, because it carries neither.</para>
///
/// <para><b>The password is often only in <c>InMemoryPassword</c>.</b> <c>Password</c> reads back empty for a SQL
/// login the user did not ask SSMS to remember, and that empty string used to be taken as "use Windows auth".</para>
/// </summary>
internal static class UIConnectionInfoReader
{
    /// <summary>
    /// Builds a connection string from a <c>UIConnectionInfo</c>, or null if it names no server.
    /// </summary>
    /// <param name="uiConnInfo">SSMS's connection info object, reached by reflection.</param>
    /// <param name="applicationName">Application Name to stamp on the connection, so it is identifiable in DMVs.</param>
    /// <param name="databaseOverride">Catalog to use instead of the one the window is on (Object Explorer wants master).</param>
    public static string BuildConnectionString(object uiConnInfo, string applicationName, string databaseOverride = null)
    {
        if (uiConnInfo == null)
            return null;

        var type = uiConnInfo.GetType();

        string server = GetString(type, uiConnInfo, "ServerName") ?? GetString(type, uiConnInfo, "ServerNameNoDot");
        if (string.IsNullOrEmpty(server))
            return null;

        string database = databaseOverride ?? GetIndexed(type, uiConnInfo, "AdvancedOptions", "DATABASE") ?? GetString(type, uiConnInfo, "DatabaseName") ?? "master";

        var builder = new SqlConnectionStringBuilder
        {
            // ADMIN: stripped - see ConnectionHelper.NormalizeHarvestedDataSource. Harvesting a DAC window's server
            // name verbatim would run the whole schema cache over the instance's single administrator connection,
            // and leave a pooled one holding it afterwards.
            DataSource = ConnectionHelper.NormalizeHarvestedDataSource(server),
            InitialCatalog = database,
            TrustServerCertificate = true, // SSMS 22 encrypts by default and self-signed certificates are the norm internally
            ConnectTimeout = 10,
            ApplicationName = applicationName,
        };

        // An Entra sign-in of any flavour - interactive, integrated, MFA, managed identity - shows up as a
        // renewable token rather than as anything a connection string can express. Leave the string
        // credential-free and let SqlConnectionFactory attach the token; see EntraTokenBroker.
        object renewableToken = GetValue(type, uiConnInfo, "RenewableToken");
        if (renewableToken != null)
        {
            EntraTokenBroker.Remember(builder.DataSource, renewableToken);
            return builder.ConnectionString;
        }

        // Except Microsoft Entra MFA, which SSMS 22 hands to SqlClient as ActiveDirectoryInteractive with no token on
        // this object at all (issue #10) - it fell through to integrated security below and failed with an SSPI error.
        // The token is borrowed from SSMS's own provider instead; see SsmsEntraProvider. The one place a number is
        // read: 5 is MFA and 4 the older Universal with MFA, both mapped to ActiveDirectoryInteractive by
        // SqlServerType.GetConnectionObject in SSMS 22's ConnectionDlg.dll.
        if (GetValue(type, uiConnInfo, "AuthenticationType") is 4 or 5)
        {
            EntraTokenBroker.RememberInteractive(builder.DataSource, GetString(type, uiConnInfo, "UserName"));
            return builder.ConnectionString;
        }

        string password = GetString(type, uiConnInfo, "Password");
        if (string.IsNullOrEmpty(password))
            password = ReadSecure(GetValue(type, uiConnInfo, "InMemoryPassword") as SecureString);

        string userName = GetString(type, uiConnInfo, "UserName");

        if (!string.IsNullOrEmpty(password))
        {
            // A SQL login on a server we hold a token for means the window was genuinely reconnected; keeping the
            // token would attach it to a connection that is now asking to be someone else.
            EntraTokenBroker.Forget(builder.DataSource);
            builder.IntegratedSecurity = false;
            builder.UserID = userName ?? "";
            builder.Password = password;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Copies a <c>SecureString</c> password out. The plaintext is unavoidable - it goes straight into a
    /// connection string - but the unmanaged copy is zeroed rather than left in the process heap.
    /// </summary>
    private static string ReadSecure(SecureString secure)
    {
        if (secure == null || secure.Length == 0)
            return null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            return Marshal.PtrToStringUni(buffer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.ZeroFreeGlobalAllocUnicode(buffer);
        }
    }

    private static object GetValue(Type type, object obj, string propName)
    {
        try
        {
            return type.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(Type type, object obj, string propName) => GetValue(type, obj, propName)?.ToString();

    /// <summary>Some values are only reachable through an indexer, e.g. <c>AdvancedOptions["DATABASE"]</c>.</summary>
    private static string GetIndexed(Type type, object obj, string collectionProp, string key)
    {
        try
        {
            object collection = GetValue(type, obj, collectionProp);
            if (collection == null)
                return null;

            // Ask for the string-keyed indexer specifically: collections with both Item[string] and Item[int]
            // otherwise throw AmbiguousMatchException.
            var collType = collection.GetType();
            var indexer = collType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public, null, typeof(string), new[] { typeof(string) }, null);
            if (indexer != null)
                return indexer.GetValue(collection, new object[] { key })?.ToString();

            foreach (var p in collType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.Name != "Item")
                    continue;
                var parameters = p.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    return p.GetValue(collection, new object[] { key })?.ToString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
