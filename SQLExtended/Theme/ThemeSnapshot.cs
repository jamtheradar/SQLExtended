using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SQLExtended.Theme;

/// <summary>
/// The palette SSMS last resolved, written to a file so programs outside SSMS can match it. SchemaCompare is the reader:
/// a standalone app can't ask SSMS for its theme (VS keeps the choice in its private settings store), and the resolved
/// palette is better than the choice anyway, because it carries the Blue and custom themes exactly.
///
/// <para>Plain <c>key=value</c> lines (<c>dark=true</c>, then one line per role) so nothing needs a JSON library. Written
/// to a temporary file and moved over the old one, so a reader never sees half a file. Pure file I/O, linked into
/// SchemaCompare and the test project.</para>
/// </summary>
internal static class ThemeSnapshot
{
    public const string FileName = "theme.ini";

    /// <summary>%APPDATA%\SQLExtended\SSMS, where the extension keeps its other settings.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SSMS");

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public static string Format(bool dark, IReadOnlyDictionary<string, string> hex)
    {
        var sb = new StringBuilder();
        sb.Append("# The SSMS theme as SQLExtended last installed it. Rewritten on every theme change.\r\n");
        sb.Append("dark=").Append(dark ? "true" : "false").Append("\r\n");
        foreach (var pair in hex)
            sb.Append(pair.Key).Append('=').Append(pair.Value).Append("\r\n");

        return sb.ToString();
    }

    public static bool TryParse(string text, out bool dark, out Dictionary<string, string> hex)
    {
        dark = true;
        hex = new Dictionary<string, string>(StringComparer.Ordinal);
        bool sawDark = false;

        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
            if (key == "dark")
            {
                dark = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                sawDark = true;
            }
            else if (value.StartsWith("#", StringComparison.Ordinal) && (value.Length == 7 || value.Length == 9))
            {
                hex[key] = value;
            }
        }

        return sawDark;
    }

    public static void Write(string path, bool dark, IReadOnlyDictionary<string, string> hex)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, Format(dark, hex), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    public static bool TryRead(string path, out bool dark, out Dictionary<string, string> hex)
    {
        dark = true;
        hex = null;
        try
        {
            return File.Exists(path) && TryParse(File.ReadAllText(path), out dark, out hex);
        }
        catch (IOException)
        {
            return false; // Mid-replace; the watcher fires again once it's done.
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
