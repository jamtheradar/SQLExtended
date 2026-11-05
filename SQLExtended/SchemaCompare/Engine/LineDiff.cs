using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SQLExtended.SchemaCompare.Engine;

public enum DiffRowKind
{
    Equal,

    /// <summary>A line on each side, paired as a change; <see cref="DiffRow.LeftSpans"/>/<see cref="DiffRow.RightSpans"/> mark the words that differ.</summary>
    Changed,

    /// <summary>A line only on the left; the right shows a filler line.</summary>
    LeftOnly,

    /// <summary>A line only on the right; the left shows a filler line.</summary>
    RightOnly,
}

/// <summary>A changed range within one line (start, length), in characters.</summary>
public struct Span
{
    public Span(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }
}

/// <summary>One row of the side-by-side view: a line on each side, or a line on one side and a filler on the other.</summary>
public sealed class DiffRow
{
    public DiffRowKind Kind { get; set; }

    /// <summary>Index into the left lines, or -1 for a filler.</summary>
    public int Left { get; set; } = -1;

    public int Right { get; set; } = -1;

    public List<Span> LeftSpans { get; } = new();

    public List<Span> RightSpans { get; } = new();
}

public sealed class DiffResult
{
    public string[] LeftLines { get; set; }
    public string[] RightLines { get; set; }
    public List<DiffRow> Rows { get; } = new();

    /// <summary>First row of each block of differences, for next/previous navigation.</summary>
    public List<int> HunkStarts { get; } = new();

    public bool HasDifferences => HunkStarts.Count > 0;
}

/// <summary>
/// Myers' O(ND) diff over lines, aligned into side-by-side rows, with a second word-level pass over each pair of
/// changed lines. Line equality goes through a key function, so "ignore case" and "ignore whitespace" decide what
/// counts as a difference while the original text is still what's shown.
/// </summary>
public static class LineDiff
{
    /// <summary>Beyond this many edits the middle of a hunk is reported as one block rather than diffed.</summary>
    /// <remarks>The trace keeps one array per edit, so memory grows with its square: 2,000 is about 32 MB at worst.</remarks>
    private const int MaxEditDistance = 2000;

    public static string[] SplitLines(string text)
        => string.IsNullOrEmpty(text) ? new string[0] : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static Func<string, string> KeyFunction(bool ignoreCase, bool ignoreWhitespace)
    {
        return line =>
        {
            if (ignoreWhitespace) line = Regex.Replace(line.Trim(), @"\s+", " ");
            return ignoreCase ? line.ToUpperInvariant() : line;
        };
    }

    public static bool AreEqual(string left, string right, bool ignoreCase, bool ignoreWhitespace)
    {
        var key = KeyFunction(ignoreCase, ignoreWhitespace);
        var a = SplitLines(left);
        var b = SplitLines(right);
        if (ignoreWhitespace)
        {
            a = a.Where(l => l.Trim().Length > 0).ToArray();
            b = b.Where(l => l.Trim().Length > 0).ToArray();
        }

        return a.Length == b.Length && a.Select(key).SequenceEqual(b.Select(key), StringComparer.Ordinal);
    }

    public static DiffResult Compute(string left, string right, bool ignoreCase, bool ignoreWhitespace)
    {
        var result = new DiffResult { LeftLines = SplitLines(left), RightLines = SplitLines(right) };
        var key = KeyFunction(ignoreCase, ignoreWhitespace);
        var ops = Diff(result.LeftLines.Select(key).ToArray(), result.RightLines.Select(key).ToArray());

        int i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                result.Rows.Add(new DiffRow { Kind = DiffRowKind.Equal, Left = ops[i].A, Right = ops[i].B });
                i++;
                continue;
            }

            // A hunk: every delete and insert up to the next equal line. Pair them up in order as changes.
            var deletes = new List<int>();
            var inserts = new List<int>();
            while (i < ops.Count && ops[i].Kind != OpKind.Equal)
            {
                if (ops[i].Kind == OpKind.Delete) deletes.Add(ops[i].A);
                else inserts.Add(ops[i].B);
                i++;
            }

            result.HunkStarts.Add(result.Rows.Count);
            int paired = Math.Min(deletes.Count, inserts.Count);
            for (int k = 0; k < paired; k++)
            {
                var row = new DiffRow { Kind = DiffRowKind.Changed, Left = deletes[k], Right = inserts[k] };
                WordDiff(result.LeftLines[row.Left], result.RightLines[row.Right], ignoreCase, row);
                result.Rows.Add(row);
            }

            for (int k = paired; k < deletes.Count; k++) result.Rows.Add(new DiffRow { Kind = DiffRowKind.LeftOnly, Left = deletes[k] });
            for (int k = paired; k < inserts.Count; k++) result.Rows.Add(new DiffRow { Kind = DiffRowKind.RightOnly, Right = inserts[k] });
        }

        return result;
    }

    // ---- word level -----------------------------------------------------------------------------------------

    private static readonly Regex WordToken = new(@"\w+|\s+|.", RegexOptions.Compiled);

    private static void WordDiff(string left, string right, bool ignoreCase, DiffRow row)
    {
        var a = WordToken.Matches(left).Cast<Match>().ToList();
        var b = WordToken.Matches(right).Cast<Match>().ToList();
        string Key(Match m) => ignoreCase ? m.Value.ToUpperInvariant() : m.Value;
        var ops = Diff(a.Select(Key).ToArray(), b.Select(Key).ToArray());

        foreach (var op in ops)
        {
            if (op.Kind == OpKind.Delete) AddSpan(row.LeftSpans, a[op.A].Index, a[op.A].Length);
            else if (op.Kind == OpKind.Insert) AddSpan(row.RightSpans, b[op.B].Index, b[op.B].Length);
        }

        // Nothing word-level to show (the lines differ only in what the key ignores): mark nothing.
    }

    private static void AddSpan(List<Span> spans, int start, int length)
    {
        if (spans.Count > 0)
        {
            var last = spans[spans.Count - 1];
            if (last.Start + last.Length == start)
            {
                spans[spans.Count - 1] = new Span(last.Start, last.Length + length);
                return;
            }
        }

        spans.Add(new Span(start, length));
    }

    // ---- Myers ----------------------------------------------------------------------------------------------

    private enum OpKind { Equal, Delete, Insert }

    private struct Op
    {
        public OpKind Kind;
        public int A, B;
    }

    private static List<Op> Diff(string[] a, string[] b)
    {
        var ops = new List<Op>();

        // Common prefix and suffix first: cheap, and most of a typical compare.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        for (int k = 0; k < prefix; k++) ops.Add(new Op { Kind = OpKind.Equal, A = k, B = k });
        ops.AddRange(Middle(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix));
        for (int k = suffix; k > 0; k--) ops.Add(new Op { Kind = OpKind.Equal, A = a.Length - k, B = b.Length - k });
        return ops;
    }

    private static List<Op> Middle(string[] a, string[] b, int aStart, int aEnd, int bStart, int bEnd)
    {
        int n = aEnd - aStart, m = bEnd - bStart;
        var ops = new List<Op>();
        if (n == 0 && m == 0) return ops;

        int max = Math.Min(n + m, MaxEditDistance);
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;

        for (int d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
        }

        if (found < 0)
        {
            // Too different to be worth aligning: everything on the left went, everything on the right came.
            for (int i = 0; i < n; i++) ops.Add(new Op { Kind = OpKind.Delete, A = aStart + i });
            for (int j = 0; j < m; j++) ops.Add(new Op { Kind = OpKind.Insert, B = bStart + j });
            return ops;
        }

        // Walk back through the trace.
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var vd = trace[d];
            int k = cx - cy;
            int prevK = (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1])) ? k + 1 : k - 1;
            int prevX = vd[offset + prevK];
            int prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                ops.Add(new Op { Kind = OpKind.Equal, A = aStart + cx, B = bStart + cy });
            }

            if (cx == prevX) ops.Add(new Op { Kind = OpKind.Insert, B = bStart + prevY });
            else ops.Add(new Op { Kind = OpKind.Delete, A = aStart + prevX });
            cx = prevX;
            cy = prevY;
        }

        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            ops.Add(new Op { Kind = OpKind.Equal, A = aStart + cx, B = bStart + cy });
        }

        ops.Reverse();

        // Within a hunk, deletes before inserts so pairing lines up the first old line with the first new one.
        var ordered = new List<Op>(ops.Count);
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                ordered.Add(ops[i++]);
                continue;
            }

            int j = i;
            while (j < ops.Count && ops[j].Kind != OpKind.Equal) j++;
            ordered.AddRange(ops.Skip(i).Take(j - i).Where(o => o.Kind == OpKind.Delete));
            ordered.AddRange(ops.Skip(i).Take(j - i).Where(o => o.Kind == OpKind.Insert));
            i = j;
        }

        return ordered;
    }
}
