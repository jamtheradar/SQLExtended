using SQLExtended.Export;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// What a mirror may treat as authoritative. Everything not named here is left exactly as it is in the target.
/// Folder names are the on-disk (sanitized) names; paths are relative to the staging root, '\'-separated.
/// </summary>
internal sealed class MirrorScope
{
    /// <summary>Database folders that exported in full. Only these have their contents replaced.</summary>
    public HashSet<string> ExportedDatabases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Database folders whose database no longer exists on the server. Their scripts are removed.</summary>
    public HashSet<string> DroppedDatabases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Database\Type Folder" pairs whose objects could not be enumerated this run. Left untouched.</summary>
    public HashSet<string> UnreadFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Database\Type Folder\file.sql" paths an object should have produced and didn't. The previous file is kept.</summary>
    public HashSet<string> UnwrittenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MirrorResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }

    /// <summary>Files kept from a previous run because this one could not produce them.</summary>
    public int Kept { get; set; }

    public bool Changed => Added + Updated + Deleted > 0;
}

/// <summary>
/// Makes a server's folder in the history match a fresh export in a staging folder — but only as far as the export
/// can vouch for. The history is a git repository, so a file missing from tonight's export is committed as an
/// object being dropped: an unreachable server, an offline database, a permission that went missing for one type, or
/// one object SMO choked on would each erase history, and the next good night would add it all back as "new". Every
/// rule here exists to stop a failure looking like a drop.
///
/// Like the window's re-export cleanup, it only ever touches .sql files inside folders named in
/// <see cref="ExportFileNaming.TypeFolders"/>, so a README or anything else in the repository is never at risk.
/// </summary>
internal static class SchemaMirror
{
    public static MirrorResult Apply(string stagingRoot, string targetRoot, MirrorScope scope)
    {
        var result = new MirrorResult();
        Directory.CreateDirectory(targetRoot);

        foreach (string database in scope.ExportedDatabases)
        {
            foreach (string type in ExportFileNaming.TypeFolders)
            {
                string relFolder = Path.Combine(database, type);
                if (scope.UnreadFolders.Contains(relFolder)) continue;

                MirrorFolder(Path.Combine(stagingRoot, relFolder), Path.Combine(targetRoot, relFolder), relFolder, scope, result);
            }

            TryDeleteIfEmpty(Path.Combine(targetRoot, database));
        }

        foreach (string database in scope.DroppedDatabases)
        {
            // A database can't be both; exported wins, since it is the one with evidence behind it.
            if (scope.ExportedDatabases.Contains(database)) continue;

            foreach (string type in ExportFileNaming.TypeFolders)
            {
                string dir = Path.Combine(targetRoot, database, type);
                foreach (string file in ScriptFilesIn(dir))
                {
                    File.Delete(file);
                    result.Deleted++;
                }
                TryDeleteIfEmpty(dir);
            }
            TryDeleteIfEmpty(Path.Combine(targetRoot, database));
        }

        return result;
    }

    private static void MirrorFolder(string source, string target, string relFolder, MirrorScope scope, MirrorResult result)
    {
        var sourceFiles = ScriptFilesIn(source).ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);
        var targetFiles = ScriptFilesIn(target).ToDictionary(Path.GetFileName, f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in targetFiles)
        {
            if (sourceFiles.ContainsKey(pair.Key)) continue;

            if (scope.UnwrittenFiles.Contains(Path.Combine(relFolder, pair.Key)))
            {
                result.Kept++;
                continue;
            }

            File.Delete(pair.Value);
            result.Deleted++;
        }

        if (sourceFiles.Count > 0) Directory.CreateDirectory(target);

        // In parallel. Almost every file is unchanged, so this is two reads per object and nothing else, and one at a
        // time it cost 18 ms a file on the first real server (8,994 files, 2m 43s): the latency of opening each file,
        // most likely an on-access antivirus scan, not the bytes. Each file is independent of every other.
        int added = 0, updated = 0;
        Parallel.ForEach(sourceFiles, new ParallelOptions { MaxDegreeOfParallelism = 8 }, pair =>
        {
            string dest = Path.Combine(target, pair.Key);
            byte[] content = File.ReadAllBytes(pair.Value);

            if (targetFiles.TryGetValue(pair.Key, out string existing))
            {
                // Rewritten only on a real difference, so an unchanged object leaves the file alone entirely. A
                // name that differs only in case (an object renamed Foo -> foo) is rewritten under the new name.
                bool sameName = string.Equals(Path.GetFileName(existing), pair.Key, StringComparison.Ordinal);
                if (sameName && SameBytes(File.ReadAllBytes(existing), content)) return;

                File.Delete(existing);
                File.WriteAllBytes(dest, content);
                Interlocked.Increment(ref updated);
            }
            else
            {
                File.WriteAllBytes(dest, content);
                Interlocked.Increment(ref added);
            }
        });
        result.Added += added;
        result.Updated += updated;

        TryDeleteIfEmpty(target);
    }

    /// <summary>
    /// The database folders already in the target: any subfolder holding at least one export type folder. Used to
    /// find databases that have since been dropped from the server.
    /// </summary>
    public static List<string> ExistingDatabaseFolders(string targetRoot)
    {
        if (!Directory.Exists(targetRoot)) return new List<string>();

        return Directory.GetDirectories(targetRoot)
            .Where(dir => Directory.GetDirectories(dir).Any(sub => ExportFileNaming.IsTypeFolder(Path.GetFileName(sub))))
            .Select(Path.GetFileName)
            .ToList();
    }

    /// <summary>Filters on the extension itself: the "*.sql" wildcard alone also matches ".sqlproj" on Windows.</summary>
    private static List<string> ScriptFilesIn(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.sql").Where(f => string.Equals(Path.GetExtension(f), ".sql", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { /* an empty folder is invisible to git anyway */ }
    }

    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
