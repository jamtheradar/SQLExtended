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

    /// <summary>Queries whose result sets are exported as files, one folder per query (or per target).</summary>
    public List<QueryConfig> Queries { get; set; } = new();

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
        Queries ??= new List<QueryConfig>();
        if (Servers.Count == 0 && ReportServers.Count == 0 && Queries.Count == 0) throw new ConfigException("servers, reportServers and queries are all empty — nothing to export.");

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

        foreach (var query in Queries) query.Validate(configDir);
        // Query names commit messages and the folder-overlap check below, so they must be distinct from each other and
        // from the servers'. (Servers and report servers have always been allowed to share a name.)
        var others = new HashSet<string>(Servers.Select(s => s.Name).Concat(ReportServers.Select(r => r.Name)), StringComparer.OrdinalIgnoreCase);
        foreach (var query in Queries)
            if (!others.Add(query.Name)) throw new ConfigException($"The query name '{query.Name}' is already used by another query or server. Give it a distinct name.");

        // Two servers sharing a folder would each see the other's databases as dropped and delete them, every night;
        // a report server owns its folder outright and would delete a schema export sharing it.
        var clash = Servers.Select(s => (s.Folder, s.Name)).Concat(ReportServers.Select(r => (r.Folder, r.Name)))
            .GroupBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (clash != null) throw new ConfigException($"Servers {string.Join(", ", clash.Select(s => $"'{s.Name}'"))} would share the folder '{clash.Key}'. Give each a distinct folder.");

        // A query owns its folder outright, like a report server, so it can't share one with anything, nor sit inside
        // or around another's: the outer one would delete the inner one's files (or see them as dropped databases).
        // A query's own targets may share a folder; that is one owner.
        var owners = Servers.Select(s => (Folder: s.Folder, Owner: s.Name))
            .Concat(ReportServers.Select(r => (Folder: r.Folder, Owner: r.Name)))
            .Concat(Queries.SelectMany(q => q.Targets.Select(t => (Folder: t.Folder, Owner: q.Name))).Distinct())
            .ToList();
        foreach (var query in Queries)
            foreach (string folder in query.Targets.Select(t => t.Folder).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var other in owners.Where(o => !string.Equals(o.Owner, query.Name, StringComparison.OrdinalIgnoreCase)))
                    if (string.Equals(folder, other.Folder, StringComparison.OrdinalIgnoreCase) || IsInside(folder, other.Folder) || IsInside(other.Folder, folder))
                        throw new ConfigException($"Query '{query.Name}' folder '{folder}' overlaps '{other.Owner}' folder '{other.Folder}'. A query owns its folder outright; give it one of its own.");
    }

    private static bool IsInside(string folder, string parent) =>
        folder.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
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

/// <summary>
/// A query whose result set is exported as files: one per row, or one for the whole result. Run against each of its
/// targets (or once, against its own connection). The folder is owned outright, like a report server's.
/// </summary>
internal sealed class QueryConfig
{
    /// <summary>Required. Names the commit and is matched by --server.</summary>
    public string Name { get; set; }

    /// <summary>The default connection for targets that don't give one, and the only one when there are no targets.</summary>
    public string ConnectionString { get; set; }

    /// <summary>Overrides the connection string's Initial Catalog. Targets can override it again.</summary>
    public string Database { get; set; }

    /// <summary>The SQL. Give this or <see cref="QueryFile"/>.</summary>
    public string Query { get; set; }

    /// <summary>A .sql file holding the query, relative to the config file.</summary>
    public string QueryFile { get; set; }

    /// <summary>Folder under outputPath; '/' or '\' make subfolders; may use {target}. Default: the name.</summary>
    public string Folder { get; set; }

    /// <summary>filePerRow (default) or singleFile.</summary>
    public string Mode { get; set; }

    /// <summary>json (default), text, csv (singleFile only) or raw (filePerRow only).</summary>
    public string Format { get; set; }

    /// <summary>
    /// filePerRow: required, a template of {Column} tokens, e.g. "{Queue}/{JobId}". singleFile: default the name.
    /// {target} works in both.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>Overrides the format's extension. "" = none.</summary>
    public string Extension { get; set; }

    public string ContentColumn { get; set; }

    public List<string> ExcludeColumns { get; set; } = new();

    /// <summary>Fewer rows than this from a target = assume the query broke and leave its folder alone. Default 1.</summary>
    public int MinRows { get; set; } = 1;

    /// <summary>
    /// A run that would delete more than this percentage of a folder's files (and more than one) is refused, unless
    /// --force. Default 25. Null turns the brake off.
    /// </summary>
    public int? MaxDeletePercent { get; set; } = 25;

    public int CommandTimeoutSeconds { get; set; } = 600;

    /// <summary>Run the same query against each of these. Empty = once, against this entry's own connection.</summary>
    public List<QueryTargetConfig> Targets { get; set; } = new();

    [JsonIgnore] public QueryOutputOptions Output { get; private set; }

    internal void Validate(string configDir)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ConfigException("Every query needs a name.");
        string where = $"Query '{Name}'";

        if (string.IsNullOrWhiteSpace(Query) == string.IsNullOrWhiteSpace(QueryFile)) throw new ConfigException($"{where}: give either query or queryFile.");
        if (!string.IsNullOrWhiteSpace(QueryFile))
        {
            string path = Path.GetFullPath(Path.Combine(configDir, Environment.ExpandEnvironmentVariables(QueryFile)));
            if (!File.Exists(path)) throw new ConfigException($"{where}: queryFile not found: {path}");
            Query = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(Query)) throw new ConfigException($"{where}: queryFile {path} is empty.");
        }

        var mode = ParseEnum<QueryExportMode>(Mode, QueryExportMode.FilePerRow, where, "mode");
        var format = ParseEnum<QueryExportFormat>(Format, QueryExportFormat.Json, where, "format");
        if (mode == QueryExportMode.SingleFile && format == QueryExportFormat.Raw) throw new ConfigException($"{where}: format raw writes one file per row; it can't be used with singleFile.");
        if (mode == QueryExportMode.FilePerRow && format == QueryExportFormat.Csv) throw new ConfigException($"{where}: format csv writes the whole result; use it with singleFile.");
        if (format == QueryExportFormat.Raw && string.IsNullOrWhiteSpace(ContentColumn)) throw new ConfigException($"{where}: format raw needs contentColumn.");

        if (mode == QueryExportMode.FilePerRow)
        {
            if (string.IsNullOrWhiteSpace(FileName)) throw new ConfigException($"{where}: filePerRow needs fileName, a template of column tokens such as \"{{JobName}}\".");
            if (!QueryOutput.TokensIn(FileName).Any(t => !string.Equals(t, QueryOutput.TargetToken, StringComparison.OrdinalIgnoreCase)))
                throw new ConfigException($"{where}: fileName \"{FileName}\" has no column token, so every row would write the same file.");
        }
        else if (string.IsNullOrWhiteSpace(FileName)) FileName = Name;

        if (MinRows < 0) throw new ConfigException($"{where}: minRows can't be negative.");
        if (MaxDeletePercent is < 0 or > 100) throw new ConfigException($"{where}: maxDeletePercent must be 0–100, or null for no brake.");
        if (CommandTimeoutSeconds < 0) throw new ConfigException($"{where}: commandTimeoutSeconds can't be negative (0 = no limit).");

        Output = new QueryOutputOptions
        {
            Mode = mode,
            Format = format,
            FileName = FileName,
            Extension = Extension,
            ContentColumn = ContentColumn,
            ExcludeColumns = ExcludeColumns ?? new List<string>(),
        };

        // No targets: one, from this entry's own connection, named after the query.
        Targets ??= new List<QueryTargetConfig>();
        if (Targets.Count == 0) Targets.Add(new QueryTargetConfig { Name = Name });
        else if (Targets.Count > 1 && Targets.Any(t => string.IsNullOrWhiteSpace(t.Name))) throw new ConfigException($"{where}: with more than one target, each needs a name.");

        string folderTemplate = string.IsNullOrWhiteSpace(Folder) ? Name : Folder;
        if (QueryOutput.TokensIn(folderTemplate).Any(t => !string.Equals(t, QueryOutput.TargetToken, StringComparison.OrdinalIgnoreCase)))
            throw new ConfigException($"{where}: folder can only use the {{{QueryOutput.TargetToken}}} token.");
        if (Path.IsPathRooted(folderTemplate)) throw new ConfigException($"{where}: folder must be relative to outputPath.");

        foreach (var target in Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Name)) target.Name = Name;
            target.ConnectionString = string.IsNullOrWhiteSpace(target.ConnectionString) ? ConnectionString : target.ConnectionString;
            if (string.IsNullOrWhiteSpace(target.ConnectionString)) throw new ConfigException($"{where}: target '{target.Name}' has no connectionString, and the query has none to fall back on.");
            if (string.IsNullOrWhiteSpace(target.Database)) target.Database = Database;

            // The same rules as a file name's segments: a target name can't add a folder or climb out of one.
            target.Folder = QueryOutput.ResolvePath(folderTemplate, extension: null, target.Name, _ => null);
        }
        if (Targets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Targets.Count) throw new ConfigException($"{where}: two targets share a name.");
    }

    private static T ParseEnum<T>(string value, T fallback, string where, string setting) where T : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse(value.Trim(), ignoreCase: true, out T parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;
        throw new ConfigException($"{where}: {setting} '{value}' isn't one of {string.Join(", ", Enum.GetNames(typeof(T)).Select(n => char.ToLowerInvariant(n[0]) + n.Substring(1)))}.");
    }
}

internal sealed class QueryTargetConfig
{
    /// <summary>The {target} token's value. Required when a query has more than one target.</summary>
    public string Name { get; set; }

    /// <summary>Default: the query's connectionString. %VARIABLES% are expanded.</summary>
    public string ConnectionString { get; set; }

    /// <summary>Default: the query's database, else the connection string's own.</summary>
    public string Database { get; set; }

    /// <summary>Resolved from the query's folder template.</summary>
    [JsonIgnore] public string Folder { get; set; }
}

internal sealed class ConfigException(string message) : Exception(message);
