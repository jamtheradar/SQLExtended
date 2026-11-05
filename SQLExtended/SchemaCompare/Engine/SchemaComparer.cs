using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.SchemaCompare.Engine;

public enum CompareStatus
{
    Different,
    LeftOnly,
    RightOnly,
    Identical,
}

/// <summary>One object in the result list: the object on each side (either may be missing) and their compared text.</summary>
public sealed class CompareItem
{
    public SchemaObjectType Type { get; set; }
    public string Schema { get; set; }
    public string Name { get; set; }
    public CompareStatus Status { get; set; }
    public SchemaObject Left { get; set; }
    public SchemaObject Right { get; set; }
    public string LeftText { get; set; }
    public string RightText { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}

public static class SchemaComparer
{
    /// <summary>
    /// Matches the two sides by object type and name (case-insensitive) and compares each pair's normalised text.
    /// Normalisation runs in parallel; it is the expensive part on a big database.
    /// </summary>
    public static List<CompareItem> Compare(SchemaSet left, SchemaSet right, CompareOptions options, CancellationToken ct = default)
    {
        var keys = left.Objects.Keys.Union(right.Objects.Keys).ToList();
        var items = new CompareItem[keys.Count];

        Parallel.For(0, keys.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            left.Objects.TryGetValue(keys[i], out var l);
            right.Objects.TryGetValue(keys[i], out var r);
            if ((l != null && options.Excludes(l)) || (r != null && options.Excludes(r))) return;

            var any = l ?? r;
            var item = new CompareItem { Type = any.Type, Schema = any.Schema, Name = any.Name, Left = l, Right = r };
            item.LeftText = l == null ? "" : Normalizer.Normalize(l, options);
            item.RightText = r == null ? "" : options.MapRightNames(Normalizer.Normalize(r, options));

            if (l == null) item.Status = CompareStatus.RightOnly;
            else if (r == null) item.Status = CompareStatus.LeftOnly;
            else item.Status = LineDiff.AreEqual(item.LeftText, item.RightText, options.IgnoreCase, options.IgnoreWhitespace) ? CompareStatus.Identical : CompareStatus.Different;

            items[i] = item;
        });

        return items.Where(i => i != null).OrderBy(i => i.Type).ThenBy(i => i.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
