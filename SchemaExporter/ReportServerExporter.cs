using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// Exports one SSRS (or Power BI Report Server) catalog into its folder in the history: every report, shared data
/// source, shared dataset and resource, one file per item, laid out by its catalog path.
///
/// Read straight from the report server's catalog database rather than through the web service: it needs only a
/// SQL login with SELECT on dbo.Catalog and dbo.DataSource, uses the same connection strings as the schema export,
/// and works on every SSRS version back to 2008. The bytes are exactly what was uploaded, which is what the web
/// portal's Download returns.
/// </summary>
internal static class ReportServerExporter
{
    // Folders (1) and linked reports (4) carry no content; Power BI reports (13) keep theirs in
    // CatalogItemExtendedContent as a .pbix that doesn't diff, so they are left out.
    private const string CatalogQuery = @"
SELECT c.Path, c.Type, CONVERT(varbinary(max), c.Content) AS Content,
       ds.Extension, ds.CredentialRetrieval, ds.Prompt, ds.Flags
FROM dbo.Catalog c
LEFT JOIN dbo.DataSource ds ON c.Type = 5 AND ds.ItemID = c.ItemID
WHERE c.Type IN (2, 3, 5, 6, 7, 8, 11, 12, 14)
ORDER BY c.Path, c.Type;";

    public static MirrorResult Export(ReportServerConfig server, ExporterConfig config, RunLog log, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(Environment.ExpandEnvironmentVariables(server.ConnectionString)) { InitialCatalog = server.Database };
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var dataSources = new List<DataSourceRow>();
        int excluded = 0;

        // Everything is read before anything is written: a failure part-way (connection, permission, timeout) throws
        // here and the folder is left exactly as it was, so it can't be committed as reports being deleted.
        using (var connection = new SqlConnection(builder.ConnectionString))
        {
            connection.Open();
            using var command = new SqlCommand(CatalogQuery, connection) { CommandTimeout = 600 };
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                string catalogPath = reader.GetString(0);
                int type = reader.GetInt32(1);

                if (server.ExcludePaths.Any(p => IsUnder(catalogPath, p))) { excluded++; continue; }

                string file = ReportMirror.FilePathFor(catalogPath, type);
                if (file == null) continue;

                // Two items mapping to one file: names that differ only by characters the file system can't hold.
                // The first keeps it; the history has one of them rather than a file that flips between the two.
                if (files.ContainsKey(file)) { log.Warn($"{server.Name}: {catalogPath} would overwrite {file} — skipped."); continue; }

                if (type == 5)
                {
                    // Written once the reader is closed: with reportServerUrl set, each one costs a web service call.
                    dataSources.Add(new DataSourceRow(file, catalogPath, reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)));
                    files.Add(file, null);
                    continue;
                }

                if (reader.IsDBNull(2)) { log.Warn($"{server.Name}: {catalogPath} has no content in the catalog — skipped."); continue; }
                files.Add(file, (byte[])reader.GetValue(2));
            }
        }

        string target = Path.Combine(config.OutputPath, server.Folder);
        WriteDataSources(server, target, dataSources, files, log, ct);
        var result = ReportMirror.Apply(target, files);

        // Every file as-is: RDL is written however the author's tool saved it, and resources are binary. Without this,
        // core.autocrlf would rewrite line endings on checkout and the files would no longer match the report server's.
        string attributes = Path.Combine(target, ReportMirror.GitAttributesFile);
        if (!File.Exists(attributes)) File.WriteAllText(attributes, "* -text\n");

        log.Info($"{server.Name}: {files.Count} item{(files.Count == 1 ? "" : "s")} — {result.Added} added, {result.Updated} changed, {result.Deleted} removed" +
            (excluded > 0 ? $" ({excluded} under excluded paths)" : "") + ".");
        return result;
    }

    private sealed record DataSourceRow(string File, string CatalogPath, string Extension, int? CredentialRetrieval, string Prompt, int? Flags);

    private const string SoapNamespace = "http://schemas.microsoft.com/sqlserver/reporting/2010/03/01/ReportServer";

    /// <summary>
    /// Fills in each shared data source's .rds. Without reportServerUrl the connection string can't be had (the catalog
    /// holds it encrypted with the report server's key), so the file records everything else. With it, the report
    /// server decrypts it for us through GetDataSourceContents. A data source whose call fails keeps its previous
    /// file, so a night the web service is down isn't committed as every connection string being removed.
    /// </summary>
    private static void WriteDataSources(ReportServerConfig server, string target, List<DataSourceRow> dataSources, Dictionary<string, byte[]> files, RunLog log, CancellationToken ct)
    {
        if (dataSources.Count == 0) return;
        if (string.IsNullOrEmpty(server.ReportServerUrl))
        {
            foreach (var ds in dataSources) files[ds.File] = DataSourceDefinition(ds, connectString: null);
            return;
        }

        // The task account's Windows credentials: the report server's own default authentication.
        using var http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = true }) { Timeout = TimeSpan.FromSeconds(60) };
        string endpoint = server.ReportServerUrl.TrimEnd('/') + "/ReportService2010.asmx";
        int failed = 0;

        foreach (var ds in dataSources)
        {
            ct.ThrowIfCancellationRequested();
            string connectString;
            try { connectString = ReportMirror.RedactPassword(GetConnectString(http, endpoint, ds.CatalogPath, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Xml.XmlException)
            {
                ct.ThrowIfCancellationRequested();

                // The first failure says why; the rest would only repeat it.
                if (failed++ == 0) log.Warn($"{server.Name}: couldn't read {ds.CatalogPath} from {endpoint} — {ex.Message}");

                string previous = Path.Combine(target, ds.File);
                files[ds.File] = File.Exists(previous) ? File.ReadAllBytes(previous) : DataSourceDefinition(ds, connectString: null);
                continue;
            }

            files[ds.File] = DataSourceDefinition(ds, connectString);
        }

        if (failed > 0) log.Warn($"{server.Name}: {failed} of {dataSources.Count} data source{(dataSources.Count == 1 ? "" : "s")} couldn't be read from the report server — their previous files are kept.");
    }

    /// <summary>ReportService2010.GetDataSourceContents: the definition with its connection string decrypted (never the password).</summary>
    private static string GetConnectString(HttpClient http, string endpoint, string catalogPath, CancellationToken ct)
    {
        string envelope = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body>" +
            $"<GetDataSourceContents xmlns=\"{SoapNamespace}\"><DataSource>{SecurityElement.Escape(catalogPath)}</DataSource></GetDataSourceContents>" +
            "</soap:Body></soap:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", $"\"{SoapNamespace}/GetDataSourceContents\"");

        using var response = http.SendAsync(request, ct).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            // A SOAP fault (no permission, no such item) comes back as a 500 with the reason in faultstring.
            string fault = null;
            try { fault = XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value; }
            catch (System.Xml.XmlException) { /* an HTML error page: the status line will do */ }
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}" + (string.IsNullOrEmpty(fault) ? "" : $": {fault}"));
        }

        var definition = XDocument.Parse(body).Descendants(XName.Get("Definition", SoapNamespace)).FirstOrDefault()
            ?? throw new InvalidOperationException("the response had no data source definition.");
        return definition.Element(XName.Get("ConnectString", SoapNamespace))?.Value ?? "";
    }

    private static bool IsUnder(string catalogPath, string excluded)
    {
        string prefix = "/" + excluded.Trim().Trim('/');
        return string.Equals(catalogPath, prefix, StringComparison.OrdinalIgnoreCase) || catalogPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A shared data source as an .rds: everything the portal's data source page shows, so a change of provider or
    /// credential mode shows in the history, plus the connection string when the report server gave it to us (null:
    /// reportServerUrl isn't set, or the call failed with no previous file to keep).
    /// </summary>
    private static byte[] DataSourceDefinition(DataSourceRow ds, string connectString)
    {
        var (catalogPath, extension, credentialRetrieval, prompt, flags) = (ds.CatalogPath, ds.Extension, ds.CredentialRetrieval, ds.Prompt, ds.Flags);
        string name = catalogPath.Substring(catalogPath.LastIndexOf('/') + 1);
        string credentials = credentialRetrieval switch { 1 => "Prompt", 2 => "Store", 3 => "Integrated", 4 => "None", null => "", _ => credentialRetrieval.ToString() };

        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        xml.Append($"<RptDataSource Name=\"{SecurityElement.Escape(name)}\">\r\n");
        xml.Append("  <ConnectionProperties>\r\n");
        xml.Append($"    <Extension>{SecurityElement.Escape(extension ?? "")}</Extension>\r\n");
        xml.Append(connectString == null
            ? "    <!-- ConnectString is encrypted by the report server; set reportServerUrl to export it. -->\r\n"
            : $"    <ConnectString>{SecurityElement.Escape(connectString)}</ConnectString>\r\n");
        xml.Append($"    <CredentialRetrieval>{credentials}</CredentialRetrieval>\r\n");
        if (credentialRetrieval == 3) xml.Append("    <IntegratedSecurity>true</IntegratedSecurity>\r\n");
        if (credentialRetrieval == 1 && !string.IsNullOrEmpty(prompt)) xml.Append($"    <Prompt>{SecurityElement.Escape(prompt)}</Prompt>\r\n");
        if (flags != null) xml.Append($"    <Flags>{flags}</Flags>\r\n");
        xml.Append("  </ConnectionProperties>\r\n");
        xml.Append("</RptDataSource>\r\n");

        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(xml.ToString())).ToArray();
    }
}
