using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLExtended.SchemaExporter;

/// <summary>The JSON config file. Property names match case-insensitively; // comments are allowed.</summary>
internal sealed class ExporterConfig
{
    /// <summary>The git working copy the tree is written into, one folder per server.</summary>
    public string OutputPath { get; set; }

    /// <summary>Where run logs go. Default: %ProgramData%\SQLExtended\SchemaExporter\logs. Never inside <see cref="OutputPath"/>.</summary>
    public string LogDirectory { get; set; }

    /// <summary>Days of run logs to keep. 0 keeps them all.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Default for servers that don't say. Needs sysadmin and the DAC — see the Decryption notes before turning it on.</summary>
    public bool DecryptEncryptedModules { get; set; }

    /// <summary>
    /// Skip a database whose schema fingerprint matches its last clean export (see SchemaFingerprint). The fingerprint
    /// costs one catalog query, against a full SMO read of the database.
    /// </summary>
    public bool SkipUnchangedDatabases { get; set; } = true;

    /// <summary>Export every database in full at least this often, fingerprint or not: the backstop for anything it misses. 0 = never.</summary>
    public int FullScanEveryDays { get; set; } = 7;

    /// <summary>Where the fingerprints are kept. Default: %ProgramData%\SQLExtended\SchemaExporter\state. Never inside <see cref="OutputPath"/>.</summary>
    public string StateDirectory { get; set; }

    public GitOptions Git { get; set; } = new();

    public List<ServerConfig> Servers { get; set; } = new();

    /// <summary>SSRS / Power BI Report Server catalogs to export, each into its own folder beside the servers'.</summary>
    public List<ReportServerConfig> ReportServers { get; set; } = new();

    public static ExporterConfig Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"Config file not found: {path}");

        ExporterConfig config;
        try { config = JsonConvert.DeserializeObject<ExporterConfig>(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new ConfigException($"{path}: {ex.Message}"); }

        if (config == null) throw new ConfigException($"{path} is empty.");
        config.Validate(Path.GetDirectoryName(Path.GetFullPath(path)));
        return config;
    }

    private void Validate(string configDir)
    {
        if (string.IsNullOrWhiteSpace(OutputPath)) throw new ConfigException("outputPath is required.");
        OutputPath = Path.GetFullPath(Path.Combine(configDir, Environment.ExpandEnvironmentVariables(OutputPath)));

        LogDirectory = string.IsNullOrWhiteSpace(LogDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SQLExtended", "SchemaExporter", "logs")
            : Path.GetFullPath(Path.Combine(configDir, Environment.ExpandEnvironmentVariables(LogDirectory)));

        // Not in the repository: a state file there would show in every folder compare against an SSMS export, and a
        // re-clone must not inherit "unchanged" for folders it may not have.
        StateDirectory = string.IsNullOrWhiteSpace(StateDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SQLExtended", "SchemaExporter", "state")
            : Path.GetFullPath(Path.Combine(configDir, Environment.ExpandEnvironmentVariables(StateDirectory)));
        if (FullScanEveryDays < 0) throw new ConfigException("fullScanEveryDays can't be negative (0 means never).");

        Git ??= new GitOptions();
        if (!string.IsNullOrWhiteSpace(Git.Executable))
        {
            Git.Executable = Path.GetFullPath(Path.Combine(configDir, Environment.ExpandEnvironmentVariables(Git.Executable)));
            if (Git.Enabled && !File.Exists(Git.Executable)) throw new ConfigException($"git.executable not found: {Git.Executable}");
        }
        Servers ??= new List<ServerConfig>();
        ReportServers ??= new List<ReportServerConfig>();
        if (Servers.Count == 0 && ReportServers.Count == 0) throw new ConfigException("servers and reportServers are both empty — nothing to export.");

        foreach (var server in Servers)
        {
            if (string.IsNullOrWhiteSpace(server.ConnectionString))
                throw new ConfigException($"Server '{server.Name}' has no connectionString.");
            if (string.IsNullOrWhiteSpace(server.Name))
                server.Name = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server.ConnectionString).DataSource;
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new ConfigException("A server has neither a name nor a Data Source in its connection string.");

            server.Folder = Export.ExportFileNaming.SanitizeFileName(string.IsNullOrWhiteSpace(server.Folder) ? server.Name : server.Folder);
            server.Databases ??= new List<string>();
            server.ExcludeDatabases ??= new List<string>();
        }

        foreach (var rs in ReportServers)
        {
            if (string.IsNullOrWhiteSpace(rs.ConnectionString))
                throw new ConfigException($"Report server '{rs.Name}' has no connectionString.");
            if (string.IsNullOrWhiteSpace(rs.Name))
                rs.Name = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(rs.ConnectionString).DataSource;
            if (string.IsNullOrWhiteSpace(rs.Name))
                throw new ConfigException("A report server has neither a name nor a Data Source in its connection string.");
            if (string.IsNullOrWhiteSpace(rs.Database)) rs.Database = "ReportServer";

            rs.Folder = Export.ExportFileNaming.SanitizeFileName(string.IsNullOrWhiteSpace(rs.Folder) ? rs.Name + "_SSRS" : rs.Folder);
            rs.ExcludePaths ??= new List<string>();

            if (!string.IsNullOrWhiteSpace(rs.ReportServerUrl))
            {
                rs.ReportServerUrl = Environment.ExpandEnvironmentVariables(rs.ReportServerUrl.Trim());
                if (!Uri.TryCreate(rs.ReportServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new ConfigException($"Report server '{rs.Name}': reportServerUrl '{rs.ReportServerUrl}' isn't an http(s) URL. It is the web service URL, e.g. http://server/ReportServer.");
            }
        }

        // Two servers sharing a folder would each see the other's databases as dropped and delete them, every night;
        // a report server owns its folder outright and would delete a schema export sharing it.
        var clash = Servers.Select(s => (s.Folder, s.Name)).Concat(ReportServers.Select(r => (r.Folder, r.Name)))
            .GroupBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash != null) throw new ConfigException($"Servers {string.Join(", ", clash.Select(s => $"'{s.Name}'"))} would share the folder '{clash.Key}'. Give each a distinct folder.");
    }
}

internal sealed class GitOptions
{
    /// <summary>Off writes the tree and stops — useful for a first look before wiring up a repository.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to git.exe. Empty runs "git" from PATH. For Portable Git, point it at &lt;folder&gt;\cmd\git.exe: that wrapper
    /// sets up the PATH its helpers (HTTPS transport, credential manager) need, which bin\ or mingw64\bin\git.exe don't.
    /// %VARIABLES% are expanded and a relative path is relative to the config file.
    /// </summary>
    public string Executable { get; set; }

    public string Remote { get; set; } = "origin";

    /// <summary>Branch to pull from and push to. Empty uses whatever the working copy has checked out.</summary>
    public string Branch { get; set; }

    public bool Push { get; set; } = true;

    /// <summary>Commit author. Empty falls back to the repository's / service account's git config.</summary>
    public string AuthorName { get; set; }

    public string AuthorEmail { get; set; }

    /// <summary>
    /// Name of an environment variable holding a PAT (Azure DevOps or GitHub). Sent as an HTTP Basic header through
    /// GIT_CONFIG_* variables, so it never appears on a command line or in .git/config. Empty uses the credential manager.
    /// </summary>
    public string TokenEnvironmentVariable { get; set; }
}

internal sealed class ServerConfig
{
    /// <summary>Display name, and the default folder. Defaults to the connection string's Data Source.</summary>
    public string Name { get; set; }

    /// <summary>Any SqlClient connection string. %VARIABLES% are expanded, so a password need not live in this file.</summary>
    public string ConnectionString { get; set; }

    /// <summary>Folder under outputPath. Defaults to <see cref="Name"/>, sanitized (a backslash in an instance name becomes '_').</summary>
    public string Folder { get; set; }

    /// <summary>Databases to export. Empty means every user database — and then a database that no longer exists has its folder removed.</summary>
    public List<string> Databases { get; set; } = new();

    public List<string> ExcludeDatabases { get; set; } = new();

    /// <summary>Overrides the top-level setting for this server.</summary>
    public bool? DecryptEncryptedModules { get; set; }
}

internal sealed class ReportServerConfig
{
    /// <summary>Display name. Defaults to the connection string's Data Source.</summary>
    public string Name { get; set; }

    /// <summary>A connection to the SQL Server holding the report server's catalog database. %VARIABLES% are expanded.</summary>
    public string ConnectionString { get; set; }

    /// <summary>The catalog database. Default ReportServer; a named SSRS instance's is often ReportServer$NAME.</summary>
    public string Database { get; set; }

    /// <summary>Folder under outputPath. Defaults to &lt;name&gt;_SSRS. The folder is owned outright: anything in it the catalog doesn't produce is deleted.</summary>
    public string Folder { get; set; }

    /// <summary>
    /// The report server's web service URL, e.g. http://PROD-SSRS01/ReportServer. Optional: when set, shared data
    /// sources are exported with their connection string, which only the report server can decrypt. Called with the
    /// task account's Windows credentials, which need permission to read data source contents (Content Manager).
    /// </summary>
    public string ReportServerUrl { get; set; }

    /// <summary>Catalog folders to leave out, with everything under them, e.g. "/Users Folders" (My Reports).</summary>
    public List<string> ExcludePaths { get; set; } = new();
}

internal sealed class ConfigException(string message) : Exception(message);
