using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SQLExtended.Formatting;

/// <summary>What a Format With… preset does to leading whitespace.</summary>
public enum IndentConversion
{
    Unchanged,
    Tabs,
    Spaces
}

/// <summary>
/// A named set of formatting rules for Format With… (issue #8), saved in <c>SQLExtendedSettings.FormatPresets</c>.
/// <see cref="FullFormat"/> is the whole formatter; every other switch is a rule that can be applied on its own.
/// </summary>
public sealed class FormatPreset
{
    public string Name { get; set; } = "";

    /// <summary>Everything Format SQL does, with the active formatter profile. Runs before the rules below.</summary>
    public bool FullFormat { get; set; }

    /// <summary>Reserved keywords. <see cref="CasingOption.Unchanged"/> means the rule is off.</summary>
    public CasingOption KeywordCase { get; set; } = CasingOption.Unchanged;

    /// <summary>Built-in function names (where called). <see cref="CasingOption.Unchanged"/> means the rule is off.</summary>
    public CasingOption FunctionCase { get; set; } = CasingOption.Unchanged;

    public IndentConversion Indentation { get; set; } = IndentConversion.Unchanged;

    /// <summary><c>[Name]</c> → <c>Name</c> where the brackets are not needed.</summary>
    public bool RemoveBrackets { get; set; }

    /// <summary><c>LEFT OUTER JOIN</c> → <c>LEFT JOIN</c> (and RIGHT, FULL).</summary>
    public bool ShortJoinKeywords { get; set; }

    /// <summary>A semicolon at the end of every statement that lacks one. Needs the text to parse.</summary>
    public bool AddSemicolons { get; set; }

    public bool TrimTrailingWhitespace { get; set; }

    public bool IsEmpty =>
        !FullFormat && KeywordCase == CasingOption.Unchanged && FunctionCase == CasingOption.Unchanged && Indentation == IndentConversion.Unchanged
        && !RemoveBrackets && !ShortJoinKeywords && !AddSemicolons && !TrimTrailingWhitespace;

    public FormatPreset Clone() => (FormatPreset)MemberwiseClone();

    /// <summary>The presets offered until the user saves their own — the two the issue names.</summary>
    public static List<FormatPreset> Defaults() => new()
    {
        new() { Name = "Casing only", KeywordCase = CasingOption.Upper, FunctionCase = CasingOption.Upper },
        new() { Name = "Full format", FullFormat = true },
    };
}

/// <summary>
/// Applies a <see cref="FormatPreset"/> to the selection (widened to whole lines) or the whole document.
///
/// <para><b>Only the full format regenerates the text.</b> The formatter rebuilds a script from ScriptDom's AST, so its
/// layout rules — line breaks, comma placement, indentation of clauses — cannot be applied one at a time: any of them
/// means all of them. What the issue's "tick only the rules you want" can honestly offer is the rules that are a
/// change to individual tokens, and those are what the other switches are. Each runs over ScriptDom's <b>token
/// stream</b>, not a regex over the text, so a keyword inside a string or a comment is never re-cased and
/// <c>[a]</c> inside a string is never unbracketed — the mistake <see cref="PostProcessor"/>'s text passes keep
/// having to be guarded against. The token stream reproduces the text exactly, so a rule that matches nothing
/// changes nothing.</para>
/// </summary>
public static class PresetFormatter
{
    public static SelectionFormatResult Format(string document, int selectionStart, int selectionEnd, FormatPreset preset, FormatterOptions options)
    {
        if (string.IsNullOrEmpty(document))
            return SelectionFormatResult.Fail("the script is empty.");
        if (preset == null || preset.IsEmpty)
            return SelectionFormatResult.Fail("no rules are ticked.");

        int start, end;
        string text;
        bool hasSelection = selectionEnd != selectionStart;

        if (preset.FullFormat)
        {
            if (hasSelection)
            {
                var widened = SelectionFormatter.Format(document, selectionStart, selectionEnd, options);
                if (!widened.Success)
                    return widened;
                (start, end, text) = (widened.Start, widened.End, widened.Replacement);
            }
            else
            {
                var whole = new SqlFormatterService(options).Format(document);
                if (!whole.Success)
                    return SelectionFormatResult.Fail(whole.ErrorMessage);
                (start, end, text) = (0, document.Length, whole.FormattedSql);
            }
        }
        else if (hasSelection)
        {
            (start, end) = WholeLines(document, Math.Min(selectionStart, selectionEnd), Math.Max(selectionStart, selectionEnd));
            text = document.Substring(start, end - start);
        }
        else
        {
            (start, end, text) = (0, document.Length, document);
        }

        var notes = new List<string>();
        text = ApplyRules(text, preset, options, notes);
        return SelectionFormatResult.Ok(start, end, text, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>The rules other than the full format, in a fixed order: token rules, semicolons, then whitespace.</summary>
    internal static string ApplyRules(string text, FormatPreset preset, FormatterOptions options, List<string> notes)
    {
        if (preset.KeywordCase != CasingOption.Unchanged || preset.RemoveBrackets || preset.ShortJoinKeywords)
            text = ApplyTokenRules(text, preset);

        if (preset.FunctionCase != CasingOption.Unchanged)
            text = PostProcessor.ApplyBuiltInFunctionCase(text, new FormatterOptions { BuiltInFunctionCase = preset.FunctionCase });

        if (preset.AddSemicolons)
            text = AddSemicolons(text, notes);

        if (preset.Indentation != IndentConversion.Unchanged || preset.TrimTrailingWhitespace)
            text = ApplyWhitespaceRules(text, preset, Math.Max(1, options?.IndentSize ?? 4));

        return text;
    }

    // ---- Token rules --------------------------------------------------------------------------------------------

    private static readonly Regex PlainIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static IList<TSqlParserToken> Tokens(string text)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        return parser.GetTokenStream(reader, out _) ?? new List<TSqlParserToken>();
    }

    private static string ApplyTokenRules(string text, FormatPreset preset)
    {
        var tokens = Tokens(text);
        if (string.Concat(tokens.Select(t => t.Text)) != text)
            return text;   // the tokenizer did not cover the text exactly; changing nothing is the safe answer

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string t = token.Text ?? "";

            if (preset.ShortJoinKeywords && IsWord(t, "OUTER") && IsOuterJoinPosition(tokens, i, out int skipTo))
            {
                i = skipTo - 1;   // drop OUTER and the whitespace after it
                continue;
            }

            if (preset.RemoveBrackets && token.TokenType == TSqlTokenType.QuotedIdentifier && t.Length > 2 && t[0] == '[')
            {
                string inner = t.Substring(1, t.Length - 2).Replace("]]", "]");
                if (PlainIdentifier.IsMatch(inner) && !PostProcessor.ReservedKeywords.Contains(inner))
                    t = inner;
            }
            else if (preset.KeywordCase != CasingOption.Unchanged && IsReservedKeyword(token))
            {
                t = preset.KeywordCase == CasingOption.Upper ? t.ToUpperInvariant() : t.ToLowerInvariant();
            }

            sb.Append(t);
        }
        return sb.ToString();
    }

    // ScriptDom gives reserved words their own token type and everything else (column names, and the many keywords that
    // are not reserved) Identifier. Casing only the former is what keeps a column called Status or Date as written.
    private static bool IsReservedKeyword(TSqlParserToken token)
    {
        if (token.TokenType == TSqlTokenType.Identifier || string.IsNullOrEmpty(token.Text))
            return false;
        foreach (char c in token.Text)
            if (!char.IsLetter(c) && c != '_')
                return false;
        return true;
    }

    // OUTER between LEFT/RIGHT/FULL and JOIN, with only whitespace and comments-free gaps around it.
    private static bool IsOuterJoinPosition(IList<TSqlParserToken> tokens, int i, out int skipTo)
    {
        skipTo = i + 1;
        int before = i - 1;
        while (before >= 0 && tokens[before].TokenType == TSqlTokenType.WhiteSpace) before--;
        if (before < 0 || !(IsWord(tokens[before].Text, "LEFT") || IsWord(tokens[before].Text, "RIGHT") || IsWord(tokens[before].Text, "FULL")))
            return false;

        int after = i + 1;
        while (after < tokens.Count && tokens[after].TokenType == TSqlTokenType.WhiteSpace) after++;
        if (after >= tokens.Count || !IsWord(tokens[after].Text, "JOIN"))
            return false;

        skipTo = after;
        return true;
    }

    private static bool IsWord(string text, string word) => string.Equals(text, word, StringComparison.OrdinalIgnoreCase);

    // ---- Semicolons ---------------------------------------------------------------------------------------------

    private sealed class StatementEnds : TSqlFragmentVisitor
    {
        public HashSet<int> Ends { get; } = new();

        public override void Visit(TSqlStatement node)
        {
            // A statement that holds statements gets its semicolons on the ones inside; one after the container too
            // would be a second semicolon at the same place (a procedure body's last statement ends where it does).
            if (node is BeginEndBlockStatement or BeginEndAtomicBlockStatement or IfStatement or WhileStatement or TryCatchStatement
                or ProcedureStatementBodyBase or TriggerStatementBody or CreateViewStatement or AlterViewStatement or CreateOrAlterViewStatement
                or LabelStatement)
                return;
            if (node.StartOffset >= 0 && node.FragmentLength > 0)
                Ends.Add(node.StartOffset + node.FragmentLength);
        }
    }

    private static string AddSemicolons(string text, List<string> notes)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        using (var reader = new StringReader(text))
            fragment = parser.Parse(reader, out IList<ParseError> errors) is var f && (errors == null || errors.Count == 0) ? f : null;
        if (fragment == null)
        {
            notes.Add("Semicolons were not added: the text does not parse.");
            return text;
        }

        var visitor = new StatementEnds();
        fragment.Accept(visitor);

        var sb = new StringBuilder(text);
        foreach (int end in visitor.Ends.OrderByDescending(e => e))
        {
            if (end <= 0 || end > text.Length || text[end - 1] == ';')
                continue;
            int next = end;
            while (next < text.Length && (text[next] == ' ' || text[next] == '\t')) next++;
            if (next < text.Length && text[next] == ';')
                continue;
            sb.Insert(end, ';');
        }
        return sb.ToString();
    }

    // ---- Whitespace ---------------------------------------------------------------------------------------------

    private static string ApplyWhitespaceRules(string text, FormatPreset preset, int indentSize)
    {
        // Lines that begin or end inside a multi-line string, quoted identifier or block comment are part of it; changing
        // their whitespace would change a string's value.
        var spans = Tokens(text)
            .Where(t => t.Text != null && t.Text.IndexOf('\n') >= 0 && t.TokenType != TSqlTokenType.WhiteSpace)
            .Select(t => (Start: t.Offset, End: t.Offset + t.Text.Length))
            .ToList();
        bool Inside(int pos) => spans.Any(s => s.Start < pos && pos < s.End);

        var sb = new StringBuilder(text.Length);
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int nl = text.IndexOf('\n', lineStart);
            int contentEnd = nl < 0 ? text.Length : (nl > lineStart && text[nl - 1] == '\r' ? nl - 1 : nl);
            string line = text.Substring(lineStart, contentEnd - lineStart);

            if (preset.Indentation != IndentConversion.Unchanged && !Inside(lineStart))
                line = ConvertIndent(line, preset.Indentation, indentSize);
            if (preset.TrimTrailingWhitespace && !Inside(contentEnd))
                line = line.TrimEnd(' ', '\t');

            sb.Append(line);
            if (nl < 0)
                break;
            sb.Append(text, contentEnd, nl + 1 - contentEnd);
            lineStart = nl + 1;
        }
        return sb.ToString();
    }

    private static string ConvertIndent(string line, IndentConversion to, int size)
    {
        int i = 0, width = 0;
        for (; i < line.Length && (line[i] == ' ' || line[i] == '\t'); i++)
            width = line[i] == '\t' ? (width / size + 1) * size : width + 1;
        if (i == 0 || i == line.Length)
            return line;

        string indent = to == IndentConversion.Tabs ? new string('\t', width / size) + new string(' ', width % size) : new string(' ', width);
        return indent + line.Substring(i);
    }

    private static (int Start, int End) WholeLines(string document, int start, int end)
    {
        int lineStart = start == 0 ? 0 : document.LastIndexOf('\n', start - 1) + 1;
        // A selection made by whole lines ends at the start of the next one, which is not part of it.
        if (end > start && document[end - 1] == '\n')
            return (lineStart, end);
        int nl = document.IndexOf('\n', end);
        int lineEnd = nl < 0 ? document.Length : (nl > 0 && document[nl - 1] == '\r' ? nl - 1 : nl);
        return (lineStart, lineEnd);
    }
}
