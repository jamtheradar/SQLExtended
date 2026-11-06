using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SQLExtended.ScriptCast;

/// <summary>One server in a Cast List and the databases ticked on it.</summary>
internal sealed class CastListServer(string server, List<string> databases)
{
    public string Server { get; } = server;
    public List<string> Databases { get; } = databases;
}

/// <summary>
/// A named target list, stored as a hand-editable JSON file (issue #13's format). Never holds credentials: the
/// writer has nowhere to put them, and the reader ignores any it finds and says so.
/// </summary>
internal sealed class CastList
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<CastListServer> Servers { get; } = new();

    /// <summary>What the reader skipped or merged. The rest of the list still loads; these are shown, not thrown.</summary>
    public List<string> Issues { get; } = new();

    public int DatabaseCount => Servers.Sum(s => s.Databases.Count);
}

/// <summary>A file that isn't a Cast List at all (bad JSON, or no <c>servers</c> array). Per-entry problems are <see cref="CastList.Issues"/> instead.</summary>
internal sealed class CastListFormatException(string message) : Exception(message);

/// <summary>Reads and writes Cast List JSON. Pure; linked into the tests.</summary>
internal static class CastListSerializer
{
    // Keys that would carry a credential if someone added one by hand. Ignored with an issue, never read.
    private static readonly string[] CredentialKeys = { "password", "pwd", "user", "userid", "user id", "uid", "login", "connectionstring", "accesstoken", "token" };

    public static CastList Parse(string json, string fallbackName = "")
    {
        JToken root;
        try
        {
            root = JToken.Parse(json ?? "");
        }
        catch (JsonReaderException ex)
        {
            throw new CastListFormatException($"Not valid JSON: {ex.Message}");
        }

        if (root is not JObject obj)
            throw new CastListFormatException("A Cast List must be a JSON object with a \"servers\" array.");

        var list = new CastList
        {
            Name = StringValue(obj, "name") ?? fallbackName ?? "",
            Description = StringValue(obj, "description") ?? "",
        };
        if (string.IsNullOrWhiteSpace(list.Name)) list.Name = fallbackName ?? "";

        if (Property(obj, "servers")?.Value is not JArray servers)
            throw new CastListFormatException("A Cast List must have a \"servers\" array.");

        var byServer = new Dictionary<string, CastListServer>(StringComparer.Ordinal);

        for (int i = 0; i < servers.Count; i++)
        {
            string where = $"servers[{i}]";
            if (servers[i] is not JObject entry)
            {
                list.Issues.Add($"{where}: not an object; skipped.");
                continue;
            }

            string server = StringValue(entry, "server")?.Trim();
            if (string.IsNullOrEmpty(server))
            {
                list.Issues.Add($"{where}: no \"server\" name; skipped.");
                continue;
            }
            where = $"{where} ({server})";

            foreach (var p in entry.Properties())
                if (CredentialKeys.Contains(p.Name.Trim().ToLowerInvariant()))
                    list.Issues.Add($"{where}: \"{p.Name}\" ignored. Cast Lists never carry credentials; the server's Object Explorer connection is used.");

            string key = CastServerName.Normalize(server);
            if (!byServer.TryGetValue(key, out var target))
            {
                target = new CastListServer(server, new List<string>());
                byServer.Add(key, target);
                list.Servers.Add(target);
            }
            else
            {
                list.Issues.Add($"{where}: server listed more than once; its databases were merged.");
            }

            var dbProp = Property(entry, "databases");
            if (dbProp == null)
            {
                list.Issues.Add($"{where}: no \"databases\" array; nothing selected on it.");
                continue;
            }
            if (dbProp.Value is not JArray dbs)
            {
                list.Issues.Add($"{where}: \"databases\" is not an array; skipped.");
                continue;
            }

            for (int d = 0; d < dbs.Count; d++)
            {
                if (dbs[d].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)dbs[d]))
                {
                    list.Issues.Add($"{where}: databases[{d}] is not a database name; skipped.");
                    continue;
                }

                string db = ((string)dbs[d]).Trim();
                if (target.Databases.Any(x => string.Equals(x, db, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Issues.Add($"{where}: database \"{db}\" listed more than once.");
                    continue;
                }
                target.Databases.Add(db);
            }
        }

        return list;
    }

    public static string Serialize(CastList list)
    {
        var root = new JObject
        {
            ["name"] = list.Name ?? "",
            ["description"] = list.Description ?? "",
            ["servers"] = new JArray(list.Servers.Select(s => new JObject
            {
                ["server"] = s.Server,
                ["databases"] = new JArray(s.Databases.Cast<object>().ToArray()),
            })),
        };
        return root.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    // JSON written by hand gets the case wrong; "Servers" is as clearly meant as "servers".
    private static JProperty Property(JObject obj, string name)
        => obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string StringValue(JObject obj, string name)
        => Property(obj, name)?.Value is JValue { Type: JTokenType.String } v ? (string)v : null;
}

/// <summary>
/// Matches a Cast List's server against Object Explorer's. A list written on one machine says
/// <c>tcp:sql-eu-01,1433</c> where another's Object Explorer shows <c>SQL-EU-01</c>; and <c>.</c>, <c>(local)</c>,
/// <c>localhost</c> and <c>127.0.0.1</c> are one server. Anything subtler (a CNAME, an AG listener vs a node) is a
/// different name and stays different.
/// </summary>
internal static class CastServerName
{
    public static string Normalize(string server)
    {
        string s = (server ?? "").Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s.Substring(prefix.Length);

        if (s.EndsWith(",1433", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);

        string host = s, instance = "";
        int slash = s.IndexOf('\\');
        if (slash >= 0) { host = s.Substring(0, slash); instance = s.Substring(slash); }

        if (host is "." or "(local)" or "localhost" or "127.0.0.1" or "(localdb)") host = host == "(localdb)" ? host : "(local)";
        return host + instance;
    }

    public static bool Same(string a, string b) => Normalize(a) == Normalize(b);
}
