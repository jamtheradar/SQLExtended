using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using System;
using System.Reflection;

namespace SQLExtended;

/// <summary>
/// Extracts the active SQL Server connection string from the current SSMS query editor window.
///
/// This relies on SSMS internal (undocumented) APIs accessed via reflection.
/// The approach is based on patterns from existing SSMS extensions:
///   - SSMSPlus (https://github.com/akarzazi/SSMSPlus)
///   - SSMSBoost
///   - SQL Judo blog series
///
/// SSMS 22 is built on VS 2026 shell. The internal DLL structure may shift
/// between point releases. All reflection calls are wrapped in try/catch
/// so the extension degrades gracefully rather than crashing SSMS.
/// </summary>
internal static class ConnectionHelper
{
    /// <summary>
    /// Returns the current database name from the active connection string, or null.
    /// </summary>
    public static string GetCurrentDatabaseName()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string connStr = GetActiveConnectionString();
            if (string.IsNullOrEmpty(connStr))
                return null;
            var builder = new SqlConnectionStringBuilder(connStr);
            return string.IsNullOrEmpty(builder.InitialCatalog) ? null : builder.InitialCatalog;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a new connection string with InitialCatalog overridden to the specified database.
    /// If targetDatabase is null, returns the original connection string unchanged.
    /// </summary>
    public static string GetConnectionStringForDatabase(string baseConnectionString, string targetDatabase)
    {
        if (string.IsNullOrEmpty(targetDatabase) || string.IsNullOrEmpty(baseConnectionString))
            return baseConnectionString;

        try
        {
            var builder = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = targetDatabase };
            return builder.ConnectionString;
        }
        catch
        {
            return baseConnectionString;
        }
    }

    /// <summary>
    /// Strips an <c>ADMIN:</c> prefix off a server name harvested from SSMS.
    ///
    /// If the active query window is a DAC window, its server name really is <c>ADMIN:HOST</c>, and copying
    /// that verbatim would put *everything* this extension does — the whole schema cache load, IntelliSense,
    /// search — onto the dedicated administrator connection. That connection exists for emergencies: it has
    /// its own limited scheduler and memory, and an instance permits exactly one at a time. Worse, our
    /// connections are pooled, so one survives the load and goes on holding the instance's only DAC slot
    /// with nothing visible in Object Explorer to show for it — which then blocks the one thing that
    /// legitimately needs a DAC, decrypting encrypted modules.
    ///
    /// The user's intent in a DAC window is still "this server", so the normal endpoint is what we use.
    /// <see cref="Decryption.DacConnectionFactory"/> puts the prefix back for the one job that needs it.
    /// </summary>
    public static string NormalizeHarvestedDataSource(string dataSource)
    {
        string source = (dataSource ?? "").Trim();
        return source.StartsWith("ADMIN:", StringComparison.OrdinalIgnoreCase) ? source.Substring("ADMIN:".Length).Trim() : source;
    }

    /// <summary>
    /// Attempts to get a usable SqlConnection string from the active SSMS query window.
    /// Returns null if no connection is available.
    /// </summary>
    public static string GetActiveConnectionString()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string connStr = TryGetServiceCache();
        if (!string.IsNullOrEmpty(connStr))
            return Harvested("ServiceCache", connStr);

        // Strategy 1: Try the ScriptFactory / CurrentlyActiveWndConnectionInfo approach
        connStr = TryGetViaScriptFactory();
        if (!string.IsNullOrEmpty(connStr))
            return Harvested("ScriptFactory", connStr);

        // Strategy 2: Try via ServiceProvider and UIConnectionInfo
        connStr = TryGetViaServiceProvider();
        if (!string.IsNullOrEmpty(connStr))
            return Harvested("ServiceProvider", connStr);

        // Every consumer reads this as "no query window is connected", which is also what it looks like when
        // all three reflection paths into SSMS's internals have quietly stopped matching.
        Diagnostics.SQLExtendedLog.Warning("Connection", "No connection could be harvested from the active window - all three strategies returned nothing.");
        return null;
    }

    /// <summary>
    /// Notes what was harvested and, when it matters, what it could not express.
    ///
    /// <para>Everything downstream - the schema cache, completion, the monitoring dashboards - reconnects from
    /// this string alone, so an authentication mode that does not survive being written into one is not a
    /// degraded connection but a failing one, and it fails at the far end with a login error that names
    /// nothing about where the credentials came from. A connection string can only spell integrated security or
    /// SQL auth with a password reflection could reach; an Entra sign-in is carried out of band, as the access
    /// token <see cref="EntraTokenBroker"/> holds and <see cref="SqlConnectionFactory"/> attaches. When even that
    /// is missing the connection arrives as integrated security against a server that has no idea what a Windows
    /// account is - the single most likely reason an Azure SQL database will not cache, and without this line
    /// there is nothing on the machine that says so.</para>
    ///
    /// <para>The string itself is never logged - it can carry a password.</para>
    /// </summary>
    private static string Harvested(string strategy, string connStr)
    {
        if (!Diagnostics.SQLExtendedLog.Enabled)
            return connStr;

        try
        {
            // Only when it changes. This is called from the poll timers (the database-change monitor, EnvTabs)
            // as well as by every command, so an unconditional note would be most of the log by volume - and
            // the file sink writes every occurrence rather than collapsing them.
            if (string.Equals(_lastHarvestNote, strategy + "|" + connStr, StringComparison.Ordinal))
                return connStr;
            _lastHarvestNote = strategy + "|" + connStr;

            var builder = new SqlConnectionStringBuilder(connStr);
            string server = builder.DataSource ?? "";
            bool hasToken = EntraTokenBroker.HasToken(server);
            string auth = hasToken
                ? "SSMS's own Entra sign-in"
                : builder.IntegratedSecurity
                    ? "integrated security"
                    : "SQL auth as " + (string.IsNullOrEmpty(builder.UserID) ? "(no user)" : builder.UserID);

            Diagnostics.SQLExtendedLog.Info("Connection", $"Harvested {server} / {builder.InitialCatalog} via {strategy}, using {auth}.");

            bool looksAzure =
                server.IndexOf(".database.windows.net", StringComparison.OrdinalIgnoreCase) >= 0
                || server.IndexOf(".database.azure.com", StringComparison.OrdinalIgnoreCase) >= 0;

            if (looksAzure && builder.IntegratedSecurity && !hasToken)
            {
                Diagnostics.SQLExtendedLog.Warning(
                    "Connection",
                    $"{server} looks like Azure SQL but the harvested connection uses integrated security, which it will refuse "
                        + "(error 40607). An Entra sign-in is normally picked up as an access token instead; landing here means SSMS "
                        + "exposed neither a token nor a readable password for this window."
                );
            }
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("Connection", $"Harvested a connection via {strategy} that could not be parsed", ex);
        }

        return connStr;
    }

    /// <summary>
    /// Primary approach: Use ScriptFactory.CurrentlyActiveWndConnectionInfo
    /// This is the most reliable method used by most SSMS extensions.
    ///
    /// The path is:
    ///   ScriptFactory.Instance → CurrentlyActiveWndConnectionInfo → UIConnectionInfo
    ///   → extract Server, Database, AuthType to build a connection string
    /// </summary>
    private static string TryGetViaScriptFactory()
    {
        try
        {
            // Load the SQLEditors assembly (contains ScriptFactory)
            // In SSMS 22 this is typically already loaded
            var sqlEditorsAssembly = FindLoadedAssembly("SQLEditors") ?? FindLoadedAssembly("Microsoft.SqlServer.Management.SqlEditor");

            if (sqlEditorsAssembly == null)
                return null;

            // Get ScriptFactory type and its singleton Instance
            var scriptFactoryType = sqlEditorsAssembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptFactory");

            if (scriptFactoryType == null)
                return null;

            var instanceProp = scriptFactoryType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var scriptFactory = instanceProp?.GetValue(null);
            if (scriptFactory == null)
                return null;

            // Get CurrentlyActiveWndConnectionInfo
            var connInfoProp = scriptFactoryType.GetProperty(
                "CurrentlyActiveWndConnectionInfo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            var connInfoWrapper = connInfoProp?.GetValue(scriptFactory);
            if (connInfoWrapper == null)
                return null;

            // The wrapper has a UIConnectionInfo property
            var uiConnInfoProp = connInfoWrapper
                .GetType()
                .GetProperty("UIConnectionInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var uiConnInfo = uiConnInfoProp?.GetValue(connInfoWrapper);
            if (uiConnInfo == null)
                return null;

            return BuildConnectionString(uiConnInfo);
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetServiceCache()
    {
        return ServiceCacheProxy.GetActiveConnectionFromServiceCache();
    }

    /// <summary>
    /// Fallback approach: Try to get connection info via the VS ServiceProvider/DTE properties.
    /// </summary>
    private static string TryGetViaServiceProvider()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
            if (dte?.ActiveDocument == null)
                return null;

            var props = dte.ActiveDocument.ProjectItem?.Properties;
            if (props == null)
                return null;

            foreach (EnvDTE.Property prop in props)
            {
                if (prop.Name.Contains("Connection") && prop.Value is string val)
                    return val;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a SqlClient connection string from a UIConnectionInfo object.
    /// The reading of it - including the Entra token an Azure sign-in carries instead of a password - lives in
    /// <see cref="UIConnectionInfoReader"/>, which all three harvest strategies share.
    /// </summary>
    private static string BuildConnectionString(object uiConnInfo) => UIConnectionInfoReader.BuildConnectionString(uiConnInfo, "SSMS Schema Viewer");

    /// <summary>Strategy and connection string of the last harvest reported, so the note is only made on a change.</summary>
    private static string _lastHarvestNote;

    private static Assembly FindLoadedAssembly(string partialName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name.Equals(partialName, StringComparison.OrdinalIgnoreCase))
                return asm;
        }
        return null;
    }
}
