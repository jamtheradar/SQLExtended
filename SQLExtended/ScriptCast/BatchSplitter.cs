using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLExtended.ScriptCast;

/// <summary>One batch of a script: the text sent to the server, how many times (<c>GO 5</c>), and its first line in the script.</summary>
internal sealed class ScriptBatch(string text, int repeatCount, int startLine)
{
    public string Text { get; } = text;
    public int RepeatCount { get; } = repeatCount;

    /// <summary>1-based line of the script the batch starts on, so a server's line number can be mapped back.</summary>
    public int StartLine { get; } = startLine;
}

/// <summary>
/// Splits a script on <c>GO</c> the way SSMS does: a line holding only <c>GO</c> (any case), optionally followed by a
/// repeat count and a <c>--</c> comment. A <c>GO</c> line inside a block comment (T-SQL nests them) or a string that
/// runs across lines is text, not a separator — the formatter's line-only splitter would cut those in two.
/// Pure; linked into the tests.
/// </summary>
internal static class BatchSplitter
{
    private static readonly Regex GoLine = new(@"^\s*go(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<ScriptBatch> Split(string script)
    {
        var batches = new List<ScriptBatch>();
        if (string.IsNullOrEmpty(script)) return batches;

        var lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var current = new StringBuilder();
        int batchStart = 1, lineCount = 0;
        int commentDepth = 0;   // nested /* */
        char openQuote = '\0';  // ' or " or ] while inside a string or quoted identifier

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (commentDepth == 0 && openQuote == '\0')
            {
                var go = GoLine.Match(line);
                if (go.Success)
                {
                    int repeat = 1;
                    if (go.Groups[1].Success && int.TryParse(go.Groups[1].Value, out int n) && n > 0) repeat = n;
                    Add(batches, current, repeat, batchStart);
                    current.Clear();
                    lineCount = 0;
                    batchStart = i + 2;
                    continue;
                }
            }

            ScanLine(line, ref commentDepth, ref openQuote);
            // Counted by line, not by length: a leading blank line has to stay a line so Add can move StartLine past it.
            if (lineCount++ > 0) current.Append("\r\n");
            current.Append(line);
        }

        Add(batches, current, 1, batchStart);
        return batches;
    }

    private static void Add(List<ScriptBatch> batches, StringBuilder text, int repeat, int startLine)
    {
        string batch = text.ToString();
        if (batch.Trim().Length == 0) return;

        // Leading blank lines are dropped so the server's line 1 is the batch's first real line; StartLine follows.
        int skipped = 0;
        while (true)
        {
            int nl = batch.IndexOf('\n');
            if (nl < 0 || batch.Substring(0, nl).Trim().Length != 0) break;
            batch = batch.Substring(nl + 1);
            skipped++;
        }

        batches.Add(new ScriptBatch(batch.TrimEnd(), repeat, startLine + skipped));
    }

    /// <summary>Advances the comment / quote state across one line. A <c>--</c> comment ends with the line.</summary>
    private static void ScanLine(string line, ref int commentDepth, ref char openQuote)
    {
        for (int j = 0; j < line.Length; j++)
        {
            char c = line[j];
            char next = j + 1 < line.Length ? line[j + 1] : '\0';

            if (openQuote != '\0')
            {
                if (c == openQuote)
                {
                    if (next == openQuote) j++;   // '' or ]] or "" is an escaped quote
                    else openQuote = '\0';
                }
                continue;
            }

            if (commentDepth > 0)
            {
                if (c == '/' && next == '*') { commentDepth++; j++; }
                else if (c == '*' && next == '/') { commentDepth--; j++; }
                continue;
            }

            switch (c)
            {
                case '-' when next == '-':
                    return;
                case '/' when next == '*':
                    commentDepth = 1; j++;
                    break;
                case '\'':
                    openQuote = '\'';
                    break;
                case '"':
                    openQuote = '"';
                    break;
                case '[':
                    openQuote = ']';
                    break;
            }
        }
    }
}
