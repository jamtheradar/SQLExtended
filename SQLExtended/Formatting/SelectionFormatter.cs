using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.Formatting;

/// <summary>What <see cref="SelectionFormatter.Format"/> decided: the span of the document to replace, and with what.</summary>
public sealed class SelectionFormatResult
{
    public bool Success { get; private set; }
    public string ErrorMessage { get; private set; }

    /// <summary>Start of the replaced span, as an offset into the document text.</summary>
    public int Start { get; private set; }

    /// <summary>End of the replaced span (exclusive).</summary>
    public int End { get; private set; }

    public string Replacement { get; private set; }

    /// <summary>Something worth telling the user alongside success — a rule that was skipped, and why.</summary>
    public string Note { get; private set; }

    internal static SelectionFormatResult Fail(string message) => new() { ErrorMessage = message };

    internal static SelectionFormatResult Ok(int start, int end, string replacement, string note = null) =>
        new() { Success = true, Start = start, End = end, Replacement = replacement, Note = note };
}

/// <summary>
/// Format Selection (issue #8): formats the statements a selection touches and nothing else, in place, at the indent they
/// already sit at.
///
/// <para><b>The selection is widened to whole statements</b>, because the formatter needs SQL that parses on its own and a
/// selection rarely is (half a WHERE, a SELECT without its FROM). The whole document is parsed once to find them, and the
/// span chosen is the deepest that stands alone: a SELECT inside a procedure body is formatted as that SELECT, not as the
/// procedure. A selection from one statement into a sibling takes both and what lies between; one that crosses a block's
/// edge (the THEN branch into the ELSE) is lifted to the enclosing statement, and so on outwards until a span parses.</para>
///
/// <para><b>The formatted text is re-indented to the first line's indent.</b> The formatter writes from column 0; pasted
/// back as it is, a statement four levels into a procedure would have its first line in place and the rest at the left
/// margin. Lines that begin inside a string literal, a quoted identifier or a block comment are left alone — padding
/// those would change a string's value.</para>
///
/// <para>If the document does not parse at all (a half-written statement elsewhere, SQLCMD lines), the selection's own
/// lines are formatted instead, which is what Format SQL would do with them, so the command still works on the good part
/// of a broken script.</para>
/// </summary>
public static class SelectionFormatter
{
    public static SelectionFormatResult Format(string document, int selectionStart, int selectionEnd, FormatterOptions options)
    {
        if (string.IsNullOrEmpty(document))
            return SelectionFormatResult.Fail("the script is empty.");

        selectionStart = Clamp(selectionStart, document.Length);
        selectionEnd = Clamp(selectionEnd, document.Length);
        if (selectionEnd < selectionStart)
            (selectionStart, selectionEnd) = (selectionEnd, selectionStart);

        var formatter = new SqlFormatterService(options);
        var statements = ParseStatements(document);

        IEnumerable<(int Start, int End)> candidates = statements == null
            ? WholeLines(document, selectionStart, selectionEnd)
            : Candidates(document, statements, selectionStart, selectionEnd);

        string firstError = null;
        foreach (var (start, end) in candidates)
        {
            var result = formatter.Format(document.Substring(start, end - start));
            if (!result.Success)
            {
                firstError ??= result.ErrorMessage;
                continue;
            }
            return SelectionFormatResult.Ok(start, end, Reindent(result.FormattedSql, LineIndent(document, start), NewLine(document)));
        }

        return SelectionFormatResult.Fail(firstError ?? (selectionStart == selectionEnd
            ? "put the caret in a statement, or select the statements to format."
            : "the selection holds no statement to format."));
    }

    // ---- Finding the span ---------------------------------------------------------------------------------------

    private readonly struct Span(int start, int end)
    {
        public int Start { get; } = start;
        public int End { get; } = end;
        public bool Contains(int pos) => Start <= pos && pos < End;
    }

    private sealed class StatementCollector : TSqlFragmentVisitor
    {
        public List<Span> Spans { get; } = new();

        public override void Visit(TSqlStatement node)
        {
            if (node.StartOffset >= 0 && node.FragmentLength > 0)
                Spans.Add(new Span(node.StartOffset, node.StartOffset + node.FragmentLength));
        }
    }

    // Every statement in the document, outermost first where two share a start. Null if the document does not parse.
    private static List<Span> ParseStatements(string document)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        using (var reader = new StringReader(document))
            fragment = parser.Parse(reader, out IList<ParseError> errors) is var f && (errors == null || errors.Count == 0) ? f : null;
        if (fragment == null)
            return null;

        var collector = new StatementCollector();
        fragment.Accept(collector);
        return collector.Spans.Distinct().OrderBy(s => s.Start).ThenByDescending(s => s.End).ToList();
    }

    // The statements containing pos, outermost first.
    private static List<Span> Chain(List<Span> statements, int pos) => statements.Where(s => s.Contains(pos)).ToList();

    /// <summary>The spans to try, narrowest first: the sibling statements the selection runs across, then each enclosing statement.</summary>
    private static IEnumerable<(int, int)> Candidates(string document, List<Span> statements, int selectionStart, int selectionEnd)
    {
        List<Span> first, last;
        if (selectionStart == selectionEnd)
        {
            // A caret just past a statement's last character (after the semicolon, at the end of the line) still means it.
            first = Chain(statements, selectionStart);
            if (first.Count == 0 && selectionStart > 0)
                first = Chain(statements, selectionStart - 1);
            if (first.Count == 0)
                yield break;
            last = first;
        }
        else
        {
            // Whitespace and comments at either end of the selection are not a reason to take in more.
            int s = SkipForward(document, selectionStart, selectionEnd);
            int e = SkipBack(document, selectionStart, selectionEnd);
            if (s >= e)
                yield break;

            int? from = Chain(statements, s).Count > 0 ? s : statements.Where(x => x.Start >= s && x.Start < e).Select(x => (int?)x.Start).FirstOrDefault();
            int? to = Chain(statements, e - 1).Count > 0 ? e - 1 : statements.Where(x => x.End <= e && x.End > s).Select(x => (int?)x.End - 1).LastOrDefault();
            if (from == null || to == null || from > to)
                yield break;

            first = Chain(statements, from.Value);
            last = Chain(statements, to.Value);
        }

        int common = 0;
        while (common < first.Count && common < last.Count && first[common].Equals(last[common]))
            common++;

        if (common < first.Count && common < last.Count)
            yield return (first[common].Start, last[common].End);

        for (int i = common - 1; i >= 0; i--)
            yield return (first[i].Start, first[i].End);
    }

    private static IEnumerable<(int, int)> WholeLines(string document, int selectionStart, int selectionEnd)
    {
        int start = document.LastIndexOf('\n', Math.Max(0, selectionStart - 1)) + 1;
        if (selectionStart == 0) start = 0;
        int end = selectionEnd == selectionStart || (selectionEnd > 0 && document[selectionEnd - 1] != '\n')
            ? IndexOfLineEnd(document, selectionEnd)
            : selectionEnd;

        int s = SkipForward(document, start, end);
        int e = SkipBack(document, start, end);
        if (s < e)
            yield return (s, e);
    }

    private static int IndexOfLineEnd(string document, int pos)
    {
        int i = document.IndexOf('\n', pos);
        if (i < 0) return document.Length;
        return i > 0 && document[i - 1] == '\r' ? i - 1 : i;
    }

    private static int SkipForward(string text, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        return from;
    }

    private static int SkipBack(string text, int from, int to)
    {
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        return to;
    }

    private static int Clamp(int value, int length) => Math.Max(0, Math.Min(length, value));

    // ---- Putting it back ----------------------------------------------------------------------------------------

    // The whitespace the line holding pos starts with.
    private static string LineIndent(string document, int pos)
    {
        int lineStart = pos == 0 ? 0 : document.LastIndexOf('\n', pos - 1) + 1;
        int i = lineStart;
        while (i < document.Length && (document[i] == ' ' || document[i] == '\t')) i++;
        return document.Substring(lineStart, i - lineStart);
    }

    private static string NewLine(string document) => document.Contains("\r\n") || !document.Contains("\n") ? "\r\n" : "\n";

    /// <summary>
    /// Puts <paramref name="indent"/> in front of every line after the first, other than blank lines and lines that start
    /// inside a string, quoted identifier or block comment. Line breaks become the document's own.
    /// </summary>
    internal static string Reindent(string formatted, string indent, string newLine)
    {
        string text = formatted.Trim().Replace("\r\n", "\n").Replace('\r', '\n');
        var protectedSpans = MultiLineTokens(text);

        var sb = new StringBuilder(text.Length + 256);
        int lineStart = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', lineStart);
            string line = nl < 0 ? text.Substring(lineStart) : text.Substring(lineStart, nl - lineStart);

            if (lineStart > 0)
            {
                sb.Append(newLine);
                bool inside = protectedSpans.Any(t => t.Start < lineStart && lineStart < t.End);
                if (!inside && indent.Length > 0 && line.Trim().Length > 0)
                    sb.Append(indent);
            }
            sb.Append(line);

            if (nl < 0)
                break;
            lineStart = nl + 1;
        }
        return sb.ToString();
    }

    private static List<Span> MultiLineTokens(string text)
    {
        var spans = new List<Span>();
        if (text.IndexOf('\n') < 0)
            return spans;

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(text))
            tokens = parser.GetTokenStream(reader, out _);
        if (tokens == null)
            return spans;

        foreach (var token in tokens)
        {
            if (token.Text == null || token.Text.IndexOf('\n') < 0)
                continue;
            if (token.TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or TSqlTokenType.QuotedIdentifier
                or TSqlTokenType.MultilineComment or TSqlTokenType.AsciiStringOrQuotedIdentifier)
                spans.Add(new Span(token.Offset, token.Offset + token.Text.Length));
        }
        return spans;
    }
}
