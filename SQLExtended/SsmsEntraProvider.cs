using Microsoft.Data.SqlClient;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended;

/// <summary>
/// Borrows the Entra sign-in SSMS itself uses for a Microsoft Entra MFA connection (issue #10).
///
/// <para>SSMS 22 gives an MFA window no <c>RenewableToken</c>. It opens its own connection as SqlClient's
/// <c>ActiveDirectoryInteractive</c>, and the token comes from <c>EntraInteractiveAuthenticationProvider</c>
/// (<c>ConnectionDlg.EntraAuthProviders.dll</c>), which SSMS registers for that method at startup. That provider
/// signs in with SSMS's own client id and MSAL cache, so once the window is connected it answers silently, for the
/// same account, with no prompt.</para>
///
/// <para><b>It is registered in SSMS's copy of SqlClient (6.x), not ours (7.x).</b> The two load side by side, and
/// registration is a static on each, so our connections never see it. Our SqlClient asking for
/// <c>ActiveDirectoryInteractive</c> on its own would sign in from scratch - a second login prompt per server, on a
/// background thread. So the provider is found in SSMS's copy and called by reflection, from our connection's
/// <c>AccessTokenCallback</c>, which hands over exactly what it needs: the authority and resource the server asked
/// for.</para>
/// </summary>
internal static class SsmsEntraProvider
{
    private static object _provider;

    /// <summary>
    /// Gets a token for the server our connection is logging in to, from SSMS's provider.
    /// </summary>
    /// <param name="parameters">What our SqlClient was asked for by the server.</param>
    /// <param name="userId">The user name on the SSMS connection, often blank for MFA - the provider then uses the account it last signed in.</param>
    public static async Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters, string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        object provider = FindProvider()
            ?? throw new InvalidOperationException("SSMS's Microsoft Entra MFA sign-in provider was not found in this SSMS build, so this connection cannot reuse its sign-in.");

        Assembly theirs = provider.GetType().BaseType?.Assembly ?? throw new InvalidOperationException("SSMS's Entra provider has no SqlClient base type.");
        Type parametersType = theirs.GetType("Microsoft.Data.SqlClient.SqlAuthenticationParameters", throwOnError: true);
        object method = Enum.Parse(theirs.GetType("Microsoft.Data.SqlClient.SqlAuthenticationMethod", throwOnError: true), "ActiveDirectoryInteractive");

        // The constructor is protected on 6.x; the argument list is the same on both sides.
        object theirParameters = Activator.CreateInstance(
            parametersType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [
                method, parameters.ServerName, parameters.DatabaseName, parameters.Resource, parameters.Authority,
                string.IsNullOrEmpty(parameters.UserId) ? userId : parameters.UserId, null, parameters.ConnectionId, parameters.ConnectionTimeout,
            ],
            null
        );

        MethodInfo acquire = provider.GetType().GetMethod("AcquireTokenAsync", [parametersType]) ?? throw new InvalidOperationException("SSMS's Entra provider has no AcquireTokenAsync.");

        // Run without our caller's synchronization context: the provider awaits without ConfigureAwait(false), so a
        // connection opened synchronously on the UI thread would otherwise wait on a continuation queued behind itself.
        // (Typed as object so Task.Run hands back the provider's task rather than unwrapping it.)
        var task = (Task)await Task.Run(() => acquire.Invoke(provider, [theirParameters])).ConfigureAwait(false);
        await task.ConfigureAwait(false);

        object token = task.GetType().GetProperty("Result").GetValue(task);
        string accessToken = token.GetType().GetProperty("AccessToken").GetValue(token) as string;
        var expiresOn = (DateTimeOffset)token.GetType().GetProperty("ExpiresOn").GetValue(token);
        return new SqlAuthenticationToken(accessToken, expiresOn);
    }

    /// <summary>
    /// The <c>ActiveDirectoryInteractive</c> provider SSMS registered, or null. Several foreign SqlClient copies can be
    /// loaded (Agent and SSIS ship their own), so the one wanted is whichever has a provider registered from outside
    /// SqlClient itself.
    /// </summary>
    private static object FindProvider()
    {
        if (_provider != null)
            return _provider;

        Assembly ours = typeof(SqlConnection).Assembly;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a != ours && a.GetName().Name == "Microsoft.Data.SqlClient"))
        {
            try
            {
                Type providerType = assembly.GetType("Microsoft.Data.SqlClient.SqlAuthenticationProvider");
                Type methodType = assembly.GetType("Microsoft.Data.SqlClient.SqlAuthenticationMethod");
                if (providerType == null || methodType == null)
                    continue;

                object method = Enum.Parse(methodType, "ActiveDirectoryInteractive");
                object provider = providerType.GetMethod("GetProvider", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, [method]);
                if (provider != null && provider.GetType().Assembly != assembly)
                {
                    Diagnostics.SQLExtendedLog.Info("Connection", $"Using SSMS's Entra MFA provider {provider.GetType().FullName} from SqlClient {assembly.GetName().Version}.");
                    return _provider = provider;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Warning("Connection", $"Could not read the Entra providers registered in {assembly.FullName}", ex);
            }
        }

        return null;
    }
}
