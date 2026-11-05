using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>
/// Decides what a chosen path is (an export folder, a database project, or any folder of scripts) and lists the .sql
/// files that make up its schema.
/// </summary>
public static class SourceLoader
{
    /// <summary>The export's type folders (<c>ExportFileNaming.TypeFolders</c>), used only to recognise an export.</summary>
    private static readonly string[] ExportFolders =
    {
        "Tables", "Views", "Stored Procedures", "Functions", "Table Types", "User-Defined Types", "Sequences", "Synonyms", "Schemas", "Database Triggers",
    };

    public sealed class Source
    {
        public string RootPath { get; set; }
        public SourceKind Kind { get; set; }
        public List<string> Files { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// <paramref name="path"/> may be a folder, a .sqlproj, or a single .sql file. A folder holding exactly one .sqlproj
    /// is read as that project.
    /// </summary>
    public static Source Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No path given.");
        path = Path.GetFullPath(path.Trim().Trim('"'));

        if (File.Exists(path))
        {
            if (path.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase)) return FromProject(path);
            return new Source { RootPath = Path.GetDirectoryName(path), Kind = SourceKind.Folder, Files = { path } };
        }

        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"'{path}' does not exist.");

        var projects = Directory.GetFiles(path, "*.sqlproj", SearchOption.TopDirectoryOnly).Where(p => p.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length == 1) return FromProject(projects[0]);

        var source = new Source
        {
            RootPath = path,
            Kind = ExportFolders.Any(f => Directory.Exists(Path.Combine(path, f))) ? SourceKind.Export : SourceKind.Folder,
            Files = AllSqlFiles(path),
        };
        if (projects.Length > 1) source.Warnings.Add($"{projects.Length} .sqlproj files in '{path}'; reading every .sql file. Choose the .sqlproj itself to read just one project.");
        return source;
    }

    /// <summary>
    /// A legacy SSDT project lists its scripts as &lt;Build Include&gt; items; an SDK-style one (Microsoft.Build.Sql)
    /// includes every .sql under it by default. A legacy project with no Build items at all (a fresh Import Database
    /// that hasn't been saved into the project yet) is read like a folder, which is what the user is looking at.
    /// Pre- and post-deployment scripts and &lt;None&gt; items are never schema.
    /// </summary>
    private static Source FromProject(string projectPath)
    {
        string root = Path.GetDirectoryName(projectPath);
        var source = new Source { RootPath = root, Kind = SourceKind.DatabaseProject };

        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (Exception ex)
        {
            source.Warnings.Add($"Could not read {Path.GetFileName(projectPath)} ({ex.Message}); reading every .sql file.");
            source.Files = AllSqlFiles(root);
            return source;
        }

        var items = doc.Descendants().Where(e => e.Parent != null && e.Parent.Name.LocalName == "ItemGroup").ToList();
        IEnumerable<string> Paths(params string[] names) => items.Where(e => names.Contains(e.Name.LocalName))
            .Select(e => (string)e.Attribute("Include") ?? (string)e.Attribute("Remove")).Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(Path.Combine(root, p.Replace('/', '\\'))));

        var excluded = new HashSet<string>(Paths("None", "PreDeploy", "PostDeploy", "Content"), StringComparer.OrdinalIgnoreCase);
        foreach (var e in items.Where(e => e.Name.LocalName == "Build" && e.Attribute("Remove") != null))
            excluded.Add(Path.GetFullPath(Path.Combine(root, ((string)e.Attribute("Remove")).Replace('/', '\\'))));

        bool sdkStyle = doc.Root?.Attribute("Sdk") != null || doc.Root?.Elements().Any(e => e.Name.LocalName == "Sdk") == true;
        var builds = items.Where(e => e.Name.LocalName == "Build" && e.Attribute("Include") != null)
            .Select(e => Path.GetFullPath(Path.Combine(root, ((string)e.Attribute("Include")).Replace('/', '\\')))).ToList();

        List<string> files;
        if (sdkStyle || builds.Count == 0)
        {
            files = AllSqlFiles(root);
            if (!sdkStyle) source.Warnings.Add($"{Path.GetFileName(projectPath)} lists no Build items; reading every .sql file in its folder.");
        }
        else
        {
            files = new List<string>();
            foreach (string build in builds)
            {
                if (build.Contains("*")) files.AddRange(ExpandWildcard(build));
                else if (File.Exists(build)) files.Add(build);
                else source.Warnings.Add($"Build item not found: {MakeRelative(root, build)}");
            }
        }

        source.Files = files.Where(f => !excluded.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return source;
    }

    private static IEnumerable<string> ExpandWildcard(string pattern)
    {
        int star = pattern.IndexOf('*');
        string dir = Path.GetDirectoryName(pattern.Substring(0, star + 1).TrimEnd('*')) ?? "";
        if (!Directory.Exists(dir)) return Enumerable.Empty<string>();
        bool recursive = pattern.Contains("**");
        return Directory.GetFiles(dir, "*.sql", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(IsSqlFile).Where(f => !InBuildOutput(dir, f));
    }

    /// <summary>Every .sql below <paramref name="root"/>, except bin\ and obj\ (a project's build output).</summary>
    public static List<string> AllSqlFiles(string root)
        => Directory.GetFiles(root, "*.sql", SearchOption.AllDirectories).Where(IsSqlFile).Where(f => !InBuildOutput(root, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    // "*.sql" also matches ".sqlproj" etc. on Windows (three-character extension patterns match longer ones).
    private static bool IsSqlFile(string f) => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static bool InBuildOutput(string root, string file)
    {
        string rel = MakeRelative(root, file);
        var first = rel.Split('\\', '/')[0];
        return first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    public static string MakeRelative(string root, string file)
    {
        string r = root.TrimEnd('\\', '/') + "\\";
        return file.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? file.Substring(r.Length) : file;
    }
}
