using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLExtended.ScriptCast;

/// <summary>One column of a result set as the reader reported it.</summary>
internal sealed class ResultColumn(string name, Type type, string sqlType)
{
    public string Name { get; } = name ?? "";
    public Type Type { get; } = type ?? typeof(object);
    public string SqlType { get; } = sqlType ?? "";
}

/// <summary>One result set read from one database.</summary>
internal sealed class ResultSetCapture(int ordinal, List<ResultColumn> columns)
{
    /// <summary>0-based position among the result sets this database returned, across all batches.</summary>
    public int Ordinal { get; } = ordinal;
    public List<ResultColumn> Columns { get; } = columns;
    public List<object[]> Rows { get; } = new();

    /// <summary>Column names and SQL types: what has to agree for two databases' rows to share a grid.</summary>
    public string Shape => string.Join("\u001f", Columns.Select(c => c.Name.ToLowerInvariant() + "\u001e" + c.SqlType.ToLowerInvariant()));
}

/// <summary>What one database returned, in the order the targets are listed.</summary>
internal sealed class DatabaseResults(string server, string database, IReadOnlyList<ResultSetCapture> resultSets)
{
    public string Server { get; } = server;
    public string Database { get; } = database;
    public IReadOnlyList<ResultSetCapture> ResultSets { get; } = resultSets;
}

/// <summary>A row of a merged grid: where it came from, then the database's own values.</summary>
internal sealed class MergedRow(string server, string database, object[] values)
{
    public string Server { get; } = server;
    public string Database { get; } = database;
    public object[] Values { get; } = values;
}

/// <summary>One grid of the results: result set N from every database whose result set N has the same columns.</summary>
internal sealed class MergedResultSet
{
    public int Ordinal { get; set; }
    public List<ResultColumn> Columns { get; set; } = new();
    public List<MergedRow> Rows { get; } = new();

    /// <summary><c>server / database</c> for each database in this grid, in target order.</summary>
    public List<string> Sources { get; } = new();

    /// <summary>
    /// This result set's columns differ from the main grid for the same ordinal. Kept apart rather than padded or
    /// coerced into the main grid: a column that lines up by position but means something else is the kind of
    /// wrong answer that looks right.
    /// </summary>
    public bool Mismatched { get; set; }
}

/// <summary>
/// Merges per-database result sets into one grid per result set (issue #13): result set N of every database goes
/// together, in target order, as long as its columns match. The shape most databases returned is the main grid
/// (ties go to the one listed first); each other shape gets its own grid, flagged. Pure; linked into the tests.
/// </summary>
internal static class ResultMerger
{
    public static List<MergedResultSet> Merge(IReadOnlyList<DatabaseResults> databases)
    {
        var merged = new List<MergedResultSet>();
        int maxOrdinal = databases.Count == 0 ? 0 : databases.Max(d => d.ResultSets.Count);

        for (int ordinal = 0; ordinal < maxOrdinal; ordinal++)
        {
            // Shapes in first-seen order, each with the databases that returned it.
            var groups = new List<(string Shape, List<(DatabaseResults Db, ResultSetCapture Set)> Members)>();
            foreach (var db in databases)
            {
                var set = db.ResultSets.FirstOrDefault(s => s.Ordinal == ordinal);
                if (set == null) continue;

                string shape = set.Shape;
                int index = groups.FindIndex(g => g.Shape == shape);
                if (index < 0) groups.Add((shape, new List<(DatabaseResults, ResultSetCapture)> { (db, set) }));
                else groups[index].Members.Add((db, set));
            }

            if (groups.Count == 0) continue;

            int main = 0;
            for (int g = 1; g < groups.Count; g++)
                if (groups[g].Members.Count > groups[main].Members.Count) main = g;

            merged.Add(Build(ordinal, groups[main].Members, mismatched: false));
            for (int g = 0; g < groups.Count; g++)
                if (g != main) merged.Add(Build(ordinal, groups[g].Members, mismatched: true));
        }

        return merged;
    }

    private static MergedResultSet Build(int ordinal, List<(DatabaseResults Db, ResultSetCapture Set)> members, bool mismatched)
    {
        var result = new MergedResultSet { Ordinal = ordinal, Columns = members[0].Set.Columns, Mismatched = mismatched };
        foreach (var (db, set) in members)
        {
            result.Sources.Add($"{db.Server} / {db.Database}");
            foreach (var row in set.Rows) result.Rows.Add(new MergedRow(db.Server, db.Database, row));
        }
        return result;
    }
}
