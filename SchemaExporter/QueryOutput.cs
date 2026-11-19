using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLExtended.SchemaExporter;

internal enum QueryExportMode { FilePerRow, SingleFile }

internal enum QueryExportFormat { Json, Text, Csv, Raw }

/// <summary>How a query's rows become files. Built from a <c>queries</c> entry by <see cref="QueryConfig"/>.</summary>
internal sealed class QueryOutputOptions
{
    public QueryExportMode Mode { get; set; } = QueryExportMode.FilePerRow;
    public QueryExportFormat Format { get; set; } = QueryExportFormat.Json;

    /// <summary>
    /// filePerRow: a template of {Column} tokens (and {target}); '/' or '\' make subfolders. singleFile: the file's
    /// name, tokens allowed only for {target}. The extension is added unless the template already ends with it.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>Overrides the format's extension (".json", ".txt", ".csv"; none for raw). "" = none.</summary>
    public string Extension { get; set; }

    /// <summary>raw only: the one column whose value is the file's content, written as-is.</summary>
    public string ContentColumn { get; set; }

    /// <summary>Left out of the content. They can still be used in <see cref="FileName"/>.</summary>
    public List<string> ExcludeColumns { get; set; } = new();
}

/// <summary>
/// Turns one result set into "relative path → bytes" for <see cref="ReportMirror.Apply"/>. Everything here is
/// deterministic: the same rows always give the same bytes, so a night when nothing changed commits nothing.
/// Columns come out in query order, nulls are explicit, and values use one invariant format each (see
/// <see cref="FormatValue"/>). Text files are UTF-8 without a BOM and CRLF.
///
/// Pure, linked into the net48 test project, so it must compile on both frameworks.
/// </summary>
internal static class QueryOutput
{
    /// <summary>The token that is always the target's name. A query may not return a column of this name.</summary>
    public const string TargetToken = "target";

    private static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string DefaultExtension(QueryExportFormat format) => format switch
    {
        QueryExportFormat.Json => ".json",
        QueryExportFormat.Text => ".txt",
        QueryExportFormat.Csv => ".csv",
        _ => "",
    };

    /// <summary>Every {token} in a template, as written.</summary>
    public static IEnumerable<string> TokensIn(string template) =>
        Token.Matches(template ?? "").Cast<Match>().Select(m => m.Groups[1].Value);

    /// <summary>
    /// Checks the result set's columns against the options. Returns the problem, or null. Run before any row is
    /// formatted, so a typo in a token is one clear error rather than every file named "_".
    /// </summary>
    public static string ValidateColumns(QueryOutputOptions options, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) return "the query returned no columns.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string column in columns)
        {
            if (string.IsNullOrWhiteSpace(column)) return "a column has no name. Give every expression an alias.";
            if (!seen.Add(column)) return $"the column '{column}' appears more than once.";
            if (string.Equals(column, TargetToken, StringComparison.OrdinalIgnoreCase))
                return $"a column is named '{column}', which is reserved for the {{{TargetToken}}} token. Alias it.";
        }

        foreach (string token in TokensIn(options.FileName))
        {
            if (string.Equals(token, TargetToken, StringComparison.OrdinalIgnoreCase)) continue;
            if (options.Mode == QueryExportMode.SingleFile) return $"fileName uses {{{token}}}, but a singleFile name can only use {{{TargetToken}}}.";
            if (!seen.Contains(token)) return $"fileName uses {{{token}}}, but the query has no column of that name.";
        }

        foreach (string column in options.ExcludeColumns ?? new List<string>())
            if (!seen.Contains(column)) return $"excludeColumns names '{column}', but the query has no column of that name.";

        if (options.Format == QueryExportFormat.Raw && !seen.Contains(options.ContentColumn ?? ""))
            return $"format raw needs contentColumn to name one of the query's columns ('{options.ContentColumn}' isn't one).";

        if (ContentColumns(options, columns).Count == 0) return "excludeColumns leaves no columns to write.";
        return null;
    }

    /// <summary>
    /// Formats the rows. Call <see cref="ValidateColumns"/> first. Two rows naming the same file (ignoring case,
    /// as Windows does): the first keeps it and <paramref name="warn"/> hears about the rest.
    /// </summary>
    public static Dictionary<string, byte[]> BuildFiles(QueryOutputOptions options, string targetName, IReadOnlyList<string> columns,
        IEnumerable<object[]> rows, Action<string> warn)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var content = ContentColumns(options, columns);
        string extension = options.Extension ?? DefaultExtension(options.Format);

        if (options.Mode == QueryExportMode.SingleFile)
        {
            string path = ResolvePath(options.FileName, extension, targetName, _ => null);
            files.Add(path, Utf8NoBom.GetBytes(SingleFile(options.Format, columns, content, rows)));
            return files;
        }

        var index = columns.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i, StringComparer.OrdinalIgnoreCase);
        int contentIndex = options.Format == QueryExportFormat.Raw ? index[options.ContentColumn] : -1;

        foreach (object[] row in rows)
        {
            string path = ResolvePath(options.FileName, extension, targetName, column => FormatValue(row[index[column]]));
            if (files.ContainsKey(path)) { warn?.Invoke($"two rows would both write {path} — the later one is skipped. Make fileName unique."); continue; }

            files.Add(path, options.Format switch
            {
                QueryExportFormat.Raw => RawBytes(row[contentIndex]),
                QueryExportFormat.Text => Utf8NoBom.GetBytes(TextRecord(columns, content, row)),
                QueryExportFormat.Json => Utf8NoBom.GetBytes(JsonDocument(writer => WriteJsonObject(writer, columns, content, row))),
                _ => throw new InvalidOperationException($"format {options.Format} can't be used with filePerRow."),
            });
        }
        return files;
    }

    /// <summary>
    /// Fills a template's tokens and makes each path segment safe: a value can't add a folder (a '/' in it becomes
    /// '_'), an empty or null value becomes "_", and ".." can't climb out of the folder.
    /// </summary>
    public static string ResolvePath(string template, string extension, string targetName, Func<string, string> columnValue)
    {
        var segments = (template ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Token.Replace(segment, m =>
            {
                string token = m.Groups[1].Value;
                string value = string.Equals(token, TargetToken, StringComparison.OrdinalIgnoreCase) ? targetName : columnValue(token);
                return string.IsNullOrEmpty(value) ? "_" : value.Replace('/', '_').Replace('\\', '_');
            }))
            .Select(segment => segment == ".." || segment == "." ? "_" : Export.ExportFileNaming.SanitizeFileName(segment))
            .ToList();
        if (segments.Count == 0) segments.Add("_");

        string last = segments[segments.Count - 1];
        if (!string.IsNullOrEmpty(extension) && !last.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            segments[segments.Count - 1] = last + extension;
        return string.Join("\\", segments);
    }

    /// <summary>
    /// One invariant text form per type, so a value only changes in the history when the data does: dates as ISO 8601
    /// with no trailing zeros, numbers with '.', GUIDs lower case, binary as base64. Null (or DBNull) is null.
    /// </summary>
    public static string FormatValue(object value) => value switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset o => o.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture).Replace(".+", "+").Replace(".-", "-"),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToBase64String(bytes),
        double f => f.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Columns written to the content, in query order: everything not excluded.</summary>
    private static List<int> ContentColumns(QueryOutputOptions options, IReadOnlyList<string> columns)
    {
        var excluded = new HashSet<string>(options.ExcludeColumns ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(0, columns.Count).Where(i => !excluded.Contains(columns[i])).ToList();
    }

    private static byte[] RawBytes(object value) => value switch
    {
        null or DBNull => Array.Empty<byte>(),
        byte[] bytes => bytes,
        _ => Utf8NoBom.GetBytes(FormatValue(value)),
    };

    private static string SingleFile(QueryExportFormat format, IReadOnlyList<string> columns, List<int> content, IEnumerable<object[]> rows)
    {
        switch (format)
        {
            case QueryExportFormat.Json:
                return JsonDocument(writer =>
                {
                    writer.WriteStartArray();
                    foreach (object[] row in rows) WriteJsonObject(writer, columns, content, row);
                    writer.WriteEndArray();
                });

            case QueryExportFormat.Csv:
                var csv = new StringBuilder();
                csv.Append(string.Join(",", content.Select(i => CsvField(columns[i])))).Append("\r\n");
                foreach (object[] row in rows) csv.Append(string.Join(",", content.Select(i => CsvField(FormatValue(row[i]))))).Append("\r\n");
                return csv.ToString();

            case QueryExportFormat.Text:
                // One record per row, a blank line between them.
                return string.Join("\r\n", rows.Select(row => TextRecord(columns, content, row)));

            default:
                throw new InvalidOperationException($"format {format} can't be used with singleFile.");
        }
    }

    /// <summary>"Column: value" lines. A null is written as NULL; a value's own line breaks are written as \n.</summary>
    private static string TextRecord(IReadOnlyList<string> columns, List<int> content, object[] row)
    {
        var text = new StringBuilder();
        foreach (int i in content)
        {
            string value = FormatValue(row[i]);
            text.Append(columns[i]).Append(": ")
                .Append(value == null ? "NULL" : value.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n"))
                .Append("\r\n");
        }
        return text.ToString();
    }

    private static string CsvField(string value)
    {
        if (value == null) return "";
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string JsonDocument(Action<JsonTextWriter> write)
    {
        var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        using (var writer = new JsonTextWriter(text) { Formatting = Newtonsoft.Json.Formatting.Indented, Indentation = 2, DateFormatHandling = DateFormatHandling.IsoDateFormat })
            write(writer);
        return text.ToString() + "\r\n";
    }

    /// <summary>
    /// Numbers and booleans as JSON numbers and booleans, everything else as the <see cref="FormatValue"/> string.
    /// Dates go through FormatValue too rather than Newtonsoft, so JSON and the other formats agree.
    /// </summary>
    private static void WriteJsonObject(JsonTextWriter writer, IReadOnlyList<string> columns, List<int> content, object[] row)
    {
        writer.WriteStartObject();
        foreach (int i in content)
        {
            writer.WritePropertyName(columns[i]);
            switch (row[i])
            {
                case null or DBNull: writer.WriteNull(); break;
                case bool b: writer.WriteValue(b); break;
                case byte or short or int or long: writer.WriteValue(Convert.ToInt64(row[i], CultureInfo.InvariantCulture)); break;
                case decimal m: writer.WriteRawValue(m.ToString(CultureInfo.InvariantCulture)); break;
                case double or float:
                    double d = Convert.ToDouble(row[i], CultureInfo.InvariantCulture);
                    if (double.IsNaN(d) || double.IsInfinity(d)) writer.WriteValue(FormatValue(row[i]));
                    else writer.WriteRawValue(FormatValue(row[i]));
                    break;
                default: writer.WriteValue(FormatValue(row[i])); break;
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// The files the mirror would delete from <paramref name="targetRoot"/>, for the delete brake: every file there
    /// (bar the folder's .gitattributes) that <paramref name="files"/> doesn't produce. Returns the count of files
    /// there now too.
    /// </summary>
    public static (int Existing, int Deleted) CountDeletions(string targetRoot, IReadOnlyDictionary<string, byte[]> files)
    {
        if (!Directory.Exists(targetRoot)) return (0, 0);
        var existing = Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(targetRoot.TrimEnd('\\', '/').Length + 1))
            .Where(rel => !string.Equals(rel, ReportMirror.GitAttributesFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (existing.Count, existing.Count(rel => !files.ContainsKey(rel)));
    }

    /// <summary>
    /// The delete brake: more than one file going and more than <paramref name="maxDeletePercent"/> of what's there.
    /// One file is never braked, so a small folder can lose a single entry. Null = no brake.
    /// </summary>
    public static bool ExceedsDeleteBrake(int existing, int deleted, int? maxDeletePercent) =>
        maxDeletePercent != null && existing > 0 && deleted > 1 && deleted * 100.0 / existing > maxDeletePercent.Value;
}
