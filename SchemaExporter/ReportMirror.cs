using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// Makes a report server's folder in the history match one complete read of its catalog. Unlike
/// <see cref="SchemaMirror"/> there is no partial result to guard: the catalog is read in one query, which either
/// returns every item or throws before this is called. So the folder is owned outright: every file in it that the
/// catalog didn't produce is deleted (resources can have any extension, so there is no narrower filter to apply).
/// The one exception is the folder's own .gitattributes, which the exporter writes.
///
/// Pure file I/O, linked into the net48 test project, so it must compile on both frameworks.
/// </summary>
internal static class ReportMirror
{
    public const string GitAttributesFile = ".gitattributes";

    /// <param name="files">Relative path ('\'-separated) to content. Paths must be unique ignoring case.</param>
    public static MirrorResult Apply(string targetRoot, IReadOnlyDictionary<string, byte[]> files)
    {
        var result = new MirrorResult();
        Directory.CreateDirectory(targetRoot);

        var existing = Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(targetRoot.TrimEnd('\\', '/').Length + 1))
            .Where(rel => !string.Equals(rel, GitAttributesFile, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(rel => rel, rel => rel, StringComparer.OrdinalIgnoreCase);

        foreach (string rel in existing.Keys.Where(rel => !files.ContainsKey(rel)).ToList())
        {
            File.Delete(Path.Combine(targetRoot, rel));
            result.Deleted++;
        }

        foreach (var pair in files)
        {
            string dest = Path.Combine(targetRoot, pair.Key);
            if (existing.TryGetValue(pair.Key, out string onDisk))
            {
                // A name that differs only in case (a report renamed Foo -> foo) is rewritten under the new name.
                bool sameName = string.Equals(onDisk, pair.Key, StringComparison.Ordinal);
                if (sameName && File.ReadAllBytes(dest).SequenceEqual(pair.Value)) continue;

                File.Delete(Path.Combine(targetRoot, onDisk));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, pair.Value);
                result.Updated++;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, pair.Value);
                result.Added++;
            }
        }

        DeleteEmptyFolders(targetRoot);
        return result;
    }

    /// <summary>
    /// The file an SSRS catalog item is written to: its catalog path as folders, plus the extension the web portal's
    /// Download gives that type. Resources and Excel workbooks keep their own name, which already carries one.
    /// Returns null for a type that has no content of its own to export.
    /// </summary>
    public static string FilePathFor(string catalogPath, int type)
    {
        string extension;
        switch (type)
        {
            case 2: extension = ".rdl"; break;       // Report
            case 3: case 14: extension = ""; break;  // Resource, Excel workbook
            case 5: extension = ".rds"; break;       // Shared data source
            case 6: extension = ".smdl"; break;      // Report model
            case 7: extension = ".rsc"; break;       // Report part
            case 8: extension = ".rsd"; break;       // Shared dataset
            case 11: extension = ".kpi"; break;      // KPI
            case 12: extension = ".rsmobile"; break; // Mobile report
            default: return null;                    // Folders, linked reports, Power BI reports, components…
        }

        var segments = (catalogPath ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Export.ExportFileNaming.SanitizeFileName).ToArray();
        if (segments.Length == 0) return null;

        string last = segments[segments.Length - 1];
        if (extension.Length > 0 && !last.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) segments[segments.Length - 1] = last + extension;
        return string.Join("\\", segments);
    }

    /// <summary>
    /// The history is pushed to a shared repository, so a password typed into the connection string itself (rather than
    /// the stored-credentials fields, which the web service never returns) is masked. A string with no password is
    /// returned exactly as written; one with a password is rebuilt by DbConnectionStringBuilder.
    /// </summary>
    public static string RedactPassword(string connectString)
    {
        if (string.IsNullOrEmpty(connectString)) return connectString;

        System.Data.Common.DbConnectionStringBuilder builder;
        try { builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectString }; }
        catch (ArgumentException) { return connectString; } // not key=value (an XML or URL data source): nothing to find

        var keys = builder.Keys.Cast<string>().Where(k => k.Equals("password", StringComparison.OrdinalIgnoreCase) || k.Equals("pwd", StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys.Count == 0) return connectString;

        foreach (string key in keys) builder[key] = "********";
        return builder.ConnectionString;
    }

    private static void DeleteEmptyFolders(string root)
    {
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch { /* an empty folder is invisible to git anyway */ }
        }
    }
}
