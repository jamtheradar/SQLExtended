using System;
using System.Collections.Generic;
using System.Text;

namespace SQLExtended.Align;

/// <summary>What <see cref="ColumnAligner.Align"/> did: the new text, and how many lines it moved a token on.</summary>
public sealed class AlignResult
{
    public string Text { get; internal set; }
    public int LinesAligned { get; internal set; }
    public bool Changed { get; internal set; }
}

/// <summary>
/// Align On (issue #8): pads the selected lines so a token — <c>AS</c>, <c>ON</c>, <c>=</c>, <c>--</c>, anything typed —
/// starts in the same column on every line that has it. A text pass, not the formatter: it works on SQL that does not
/// parse, and touches nothing but the whitespace in front of the token.
///
/// <para>Rules, each of which keeps it off a token that only looks like the one asked for:</para>
/// <list type="bullet">
/// <item>Strings, quoted identifiers, <c>[brackets]</c> and comments are skipped, with their state carried from line to
/// line, so a block comment or string spanning lines is skipped throughout. <c>--</c> is the exception: it matches the
/// start of a line comment, which is what aligning trailing comments means.</item>
/// <item>A word token matches whole words only, ignoring case (<c>AS</c> is not in <c>CAST</c> or <c>ASC</c>). An
/// operator token does not match inside a longer operator (<c>=</c> is not in <c>&gt;=</c> or <c>+=</c>).</item>
/// <item>Nothing inside parentheses opened on the same line: in <c>CAST(x AS int) AS y</c> the alias is the AS meant,
/// not the cast's. Except with every occurrence, which is how the commas inside <c>VALUES (…)</c> rows line up.</item>
/// <item>A token that is the first thing on its line is passed over (a leading comma, a comment-only line) — it is
/// already placed by the line's indent, and aligning it would push the line's whole content across.</item>
/// </list>
///
/// <para>The text in front of the token is trimmed and re-padded rather than added to, so aligning twice changes nothing
/// and a block aligned too far out is pulled back in. Each line keeps its own gap — one space if it had any whitespace
/// before the token, none if not — and the column is the furthest that fits every line's gap in: <c>a = 1</c> keeps its
/// space, and a comma written tight against its item stays tight on the longest line.</para>
/// </summary>
public static class ColumnAligner
{
    private const string OperatorChars = "<>!=+-*/%&|^~";

    public static AlignResult Align(string text, string token, bool everyOccurrence = false, bool useTabs = false, int tabSize = 4)
    {
        var result = new AlignResult { Text = text ?? "" };
        token = token?.Trim();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
            return result;
        if (tabSize < 1)
            tabSize = 4;

        var matcher = new Matcher(token, insideParentheses: everyOccurrence);
        var (lines, endings) = SplitLines(text);

        // The lexer state each line starts in. Padding is only ever inserted in code, so these stay valid as lines change.
        var startStates = new LexState[lines.Count];
        var state = default(LexState);
        for (int i = 0; i < lines.Count; i++)
        {
            startStates[i] = state;
            state = Scan(lines[i], state, matcher, int.MaxValue, out _);
        }

        var searchFrom = new int[lines.Count];
        var aligned = new bool[lines.Count];
        var matches = new List<(int Line, int Pos, string Prefix, int Width, int Gap)>();

        while (true)
        {
            matches.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                int pos = FindNext(lines[i], startStates[i], matcher, searchFrom[i]);
                if (pos < 0)
                    continue;
                string prefix = lines[i].Substring(0, pos).TrimEnd(' ', '\t');
                matches.Add((i, pos, prefix, VisualWidth(prefix, tabSize), prefix.Length < pos ? 1 : 0));
            }

            if (matches.Count < 2)
                break;

            int target = 0;
            foreach (var m in matches)
                target = Math.Max(target, m.Width + m.Gap);
            if (useTabs && target % tabSize != 0)
                target = (target / tabSize + 1) * tabSize;

            foreach (var m in matches)
            {
                string padded = m.Prefix + Pad(m.Width, target, useTabs, tabSize);
                string line = padded + lines[m.Line].Substring(m.Pos);
                if (line != lines[m.Line])
                {
                    lines[m.Line] = line;
                    aligned[m.Line] = true;
                }
                searchFrom[m.Line] = padded.Length + matcher.Length;
            }

            if (!everyOccurrence)
                break;
        }

        var sb = new StringBuilder(text.Length + 64);
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append(endings[i]);
            if (aligned[i])
                result.LinesAligned++;
        }

        result.Text = sb.ToString();
        result.Changed = result.LinesAligned > 0;
        return result;
    }

    // The next occurrence at or after `from` that is not the first thing on the line.
    private static int FindNext(string line, LexState start, Matcher matcher, int from)
    {
        while (true)
        {
            Scan(line, start, matcher, from, out int pos);
            if (pos < 0)
                return -1;
            if (!string.IsNullOrWhiteSpace(line.Substring(0, pos)))
                return pos;
            from = pos + matcher.Length;
        }
    }

    private enum Mode { Code, BlockComment, SingleQuote, DoubleQuote, Bracket }

    private struct LexState
    {
        public Mode Mode;
        public int CommentDepth;   // T-SQL block comments nest
    }

    /// <summary>
    /// Lexes one line from <paramref name="state"/>, reporting in <paramref name="found"/> the first token match at or
    /// after <paramref name="from"/> (or -1), and returning the state the next line starts in.
    /// </summary>
    private static LexState Scan(string line, LexState state, Matcher matcher, int from, out int found)
    {
        found = -1;
        int depth = 0;   // parentheses opened on this line
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            switch (state.Mode)
            {
                case Mode.BlockComment:
                    if (c == '/' && next == '*') { state.CommentDepth++; i += 2; }
                    else if (c == '*' && next == '/') { if (--state.CommentDepth == 0) state.Mode = Mode.Code; i += 2; }
                    else i++;
                    continue;
                case Mode.SingleQuote:
                    i += CloseQuote(c, next, '\'', ref state);
                    continue;
                case Mode.DoubleQuote:
                    i += CloseQuote(c, next, '"', ref state);
                    continue;
                case Mode.Bracket:
                    i += CloseQuote(c, next, ']', ref state);
                    continue;
            }

            if (c == '-' && next == '-')
            {
                if (matcher.IsLineComment && i >= from)
                    found = i;
                return state;   // the rest of the line is comment
            }
            if (c == '/' && next == '*') { state.Mode = Mode.BlockComment; state.CommentDepth = 1; i += 2; continue; }
            if (c == '\'') { state.Mode = Mode.SingleQuote; i++; continue; }
            if (c == '"') { state.Mode = Mode.DoubleQuote; i++; continue; }
            if (c == '[') { state.Mode = Mode.Bracket; i++; continue; }

            if (found < 0 && i >= from && (depth <= 0 || matcher.InsideParentheses) && matcher.MatchesAt(line, i))
            {
                found = i;
                return state;   // callers that want a match do not use the state
            }

            if (c == '(') depth++;
            else if (c == ')') depth--;
            i++;
        }
        return state;
    }

    // A doubled closing character is an escaped one, not the end.
    private static int CloseQuote(char c, char next, char close, ref LexState state)
    {
        if (c != close)
            return 1;
        if (next == close)
            return 2;
        state.Mode = Mode.Code;
        return 1;
    }

    private sealed class Matcher
    {
        private readonly string _token;
        private readonly bool _wordStart, _wordEnd, _operatorStart, _operatorEnd;

        public Matcher(string token, bool insideParentheses)
        {
            _token = token;
            InsideParentheses = insideParentheses;
            IsLineComment = token == "--";
            _wordStart = IsWordChar(token[0]);
            _wordEnd = IsWordChar(token[token.Length - 1]);
            _operatorStart = OperatorChars.IndexOf(token[0]) >= 0;
            _operatorEnd = OperatorChars.IndexOf(token[token.Length - 1]) >= 0;
        }

        public bool IsLineComment { get; }
        public bool InsideParentheses { get; }
        public int Length => _token.Length;

        public bool MatchesAt(string line, int i)
        {
            if (IsLineComment || i + _token.Length > line.Length)
                return false;
            if (string.Compare(line, i, _token, 0, _token.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            char before = i > 0 ? line[i - 1] : ' ';
            char after = i + _token.Length < line.Length ? line[i + _token.Length] : ' ';
            if (_wordStart && IsWordChar(before)) return false;
            if (_wordEnd && IsWordChar(after)) return false;
            if (_operatorStart && OperatorChars.IndexOf(before) >= 0) return false;
            if (_operatorEnd && "=<>".IndexOf(after) >= 0) return false;
            return true;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';
    }

    private static int VisualWidth(string s, int tabSize)
    {
        int col = 0;
        foreach (char c in s)
            col = c == '\t' ? (col / tabSize + 1) * tabSize : col + 1;
        return col;
    }

    private static string Pad(int from, int to, bool useTabs, int tabSize)
    {
        if (!useTabs)
            return new string(' ', Math.Max(0, to - from));

        var sb = new StringBuilder();
        for (int col = from; col < to; col = (col / tabSize + 1) * tabSize)
            sb.Append('\t');
        return sb.ToString();
    }

    private static (List<string> Lines, List<string> Endings) SplitLines(string text)
    {
        var lines = new List<string>();
        var endings = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
                continue;
            int len = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(text.Substring(start, i - start));
            endings.Add(text.Substring(i, len));
            i += len - 1;
            start = i + 1;
        }
        lines.Add(text.Substring(start));
        endings.Add("");
        return (lines, endings);
    }
}
