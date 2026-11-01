using Microsoft.Data.SqlClient;
using System;

namespace SQLExtended;

/// <summary>
/// The one place a <see cref="SqlConnection"/> is created from a harvested connection string.
///
/// <para>An Entra (Azure AD) sign-in cannot be written into a connection string, so
/// <see cref="UIConnectionInfoReader"/> harvests those servers credential-free and parks the access token in
/// <see cref="EntraTokenBroker"/>. The token is attached here, on the way to <c>Open</c>. Anything that news up a
/// <c>SqlConnection</c> directly gets a string with no credentials at all and fails to log in — which is why every
/// caller goes through this method, and why new ones must.</para>
///
/// <para>The one deliberate exception is <see cref="Decryption.DacConnectionFactory"/>: the dedicated administrator
/// connection is an on-premises feature that Azure SQL Database does not have, so no token can apply to it.</para>
/// </summary>
internal static class SqlConnectionFactory
{
    /// <summary>Creates an unopened connection, attaching a harvested Entra token when the string carries no credentials of its own.</summary>
    public static SqlConnection Create(string connectionString)
    {
        var connection = new SqlConnection(connectionString);

        try
        {
            if (!WantsAccessToken(connectionString))
                return connection;

            string dataSource = new SqlConnectionStringBuilder(connectionString).DataSource;
            string token = EntraTokenBroker.TryGetAccessToken(dataSource);
            if (!string.IsNullOrEmpty(token))
                connection.AccessToken = token;
            else if (EntraTokenBroker.TryGetAccessTokenCallback(dataSource) is { } callback)
                connection.AccessTokenCallback = callback;
        }
        catch (Exception ex)
        {
            // Never take the connection down with us - without a token it fails at login, with a message the
            // caller already reports, rather than here with a reflection error nobody is watching for.
            Diagnostics.SQLExtendedLog.Warning("Connection", "Could not attach a harvested Entra access token to a new connection", ex);
        }

        return connection;
    }

    /// <summary>
    /// A token may only be attached to a string that expresses no other authentication - SqlClient throws if it
    /// is combined with integrated security, a user id, a password or an <c>Authentication=</c> mode. That is
    /// exactly the shape the Entra harvest produces, so this doubles as the test for "was this harvested as Entra".
    /// </summary>
    private static bool WantsAccessToken(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        return !builder.IntegratedSecurity
            && string.IsNullOrEmpty(builder.UserID)
            && string.IsNullOrEmpty(builder.Password)
            && builder.Authentication == SqlAuthenticationMethod.NotSpecified;
    }
}
