using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLExtended.Formatting;

/// <summary>
/// Post-processes ScriptGenerator output for formatting options it doesn't natively support.
/// ScriptGenerator produces predictable, structured output, making regex/line manipulation safe.
/// </summary>
public static class PostProcessor
{
    /// <param name="trailingComments">
    /// The single-line comments the *source* carried at the end of a line of code, counted by text
    /// (<see cref="SqlFormatterService.CollectTrailingComments"/>). <see cref="RestoreCommentLinePlacement"/>
    /// needs it and cannot work without it — see that method. Null means "not known", and then no comment
    /// is moved in either direction, which is the harmless default.
    /// </param>
    public static string Apply(string sql, FormatterOptions options, IDictionary<string, int> trailingComments = null)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        string result = sql;

        // Apply indent style (tabs vs spaces) — do this first since other transforms may add indentation
        result = ApplyIndentStyle(result, options);

        // Put each comment back on the line the source had it on — ScriptDom moves them both ways
        result = RestoreCommentLinePlacement(result, trailingComments);

        // SET left-aligned with its UPDATE. Runs before the CTE/SELECT/comma passes so everything
        // downstream sees the set clause at its final indentation.
        if (options.AlignSetWithUpdate)
            result = ApplySetClauseAlignment(result, options);

        // CTE stacked layout — reflow WITH ... AS ( ... ) blocks structurally. Run before
        // the SELECT-column and comma passes so their bodies get normalized too.
        if (options.CteStackedLayout)
            result = ApplyCteStackedLayout(result, options);

        // Derived tables reflowed to the same stacked shape. Also before the SELECT-column and comma
        // passes, so a subquery body is normalized by them like any other query.
        if (options.DerivedTableStackedLayout)
            result = ApplyDerivedTableStackedLayout(result, options);

        // SELECT keyword alone + one column per indented line
        if (options.SelectColumnLayout == SelectColumnLayoutOption.StackedFirstOnNewLine)
            result = ApplyStackSelectColumns(result, options);

        // Alias style (AS vs no AS vs "alias = expression"). Sits here, ahead of the CASE pass, because
        // ColumnEquals moves the expression sideways — the alias is lifted from the tail of the item to the
        // front of it — and the CASE pass aligns a reflowed body to the column its CASE keyword occupies.
        // Reflowing first left every WHEN and END of an aliased CASE lined up on where the CASE used to be.
        result = ApplyAliasStyle(result, options);

        // CASE expressions reflowed so every WHEN starts a line. Before the comma pass, which decides the
        // side of the break a separator lands on by comparing the indent of consecutive lines — and refuses
        // where the earlier line is more deeply indented, which is exactly what ScriptDom's run-on CASE
        // produces. After the column stacking, which re-indents every depth-0 line under a SELECT and would
        // flatten the WHENs back into a column list.
        result = ApplyCaseWhenLayout(result, options);

        // Comma placement (leading vs trailing)
        if (options.CommaPosition == CommaPositionOption.LeadingComma)
            result = ApplyLeadingCommas(result, options);

        // Collapse ScriptDom's split JOIN layout (JOIN keyword + table on separate lines)
        if (options.JoinLayout == JoinLayoutOption.NewLine)
            result = CollapseJoinLayout(result, options);

        // Normalize JOIN keywords: LEFT/RIGHT OUTER JOIN -> LEFT/RIGHT JOIN
        if (options.NormalizeJoinKeywords)
            result = ApplyNormalizeJoinKeywords(result);

        // Align FROM and JOIN keywords at the same indentation level
        if (options.AlignFromAndJoins)
            result = ApplyAlignFromAndJoins(result, options);

        // WHERE AND/OR alignment (left-align continuations with the WHERE keyword)
        if (!options.IndentBetweenConditions && options.WhereConditionLayout == WhereConditionLayoutOption.NewLinePerCondition)
            result = ApplyWhereConditionAlignment(result);

        // Bracket quoting
        result = ApplyBracketQuoting(result, options);

        // JOIN ON same line
        if (options.JoinOnSameLine)
            result = ApplyJoinOnSameLine(result);

        // Identifier casing
        result = ApplyIdentifierCase(result, options);

        // Built-in function casing (ROW_NUMBER, SUM, GETDATE, ...)
        result = ApplyBuiltInFunctionCase(result, options);

        // INSERT column/value wrapping
        result = ApplyInsertWrapping(result, options);

        // Procedure/function parameter wrapping
        if (options.ProcedureParametersOnSameLine)
            result = ApplyProcedureParameterWrapping(result, options);

        // Blank line before DML statements
        if (options.BlankLineBeforeStatement)
            result = ApplyBlankLineBeforeStatements(result);

        // Blank lines between statements
        result = ApplyBlankLinesBetweenStatements(result, options);

        // Semicolons
        result = ApplySemicolons(result, options);

        // Max line width — soft wrapping
        // (ScriptGenerator generally respects reasonable widths; we apply as a final pass)

        return result;
    }

    private static string ApplyIndentStyle(string sql, FormatterOptions options)
    {
        // ScriptGenerator uses spaces by default. Convert to tabs if requested.
        if (options.IndentStyle == IndentStyleOption.Tabs)
        {
            var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int spaceCount = 0;
                while (spaceCount < line.Length && line[spaceCount] == ' ')
                    spaceCount++;

                if (spaceCount > 0 && options.IndentSize > 0)
                {
                    int tabCount = spaceCount / options.IndentSize;
                    int remainder = spaceCount % options.IndentSize;
                    string indent = new string('\t', tabCount) + new string(' ', remainder);
                    line = indent + line.Substring(spaceCount);
                }

                if (i > 0) sb.AppendLine();
                sb.Append(line);
            }

            return sb.ToString();
        }

        return sql;
    }

    /// <summary>
    /// ScriptDom splits a comment that trailed a line of code onto its own line:
    ///     Name NVARCHAR(500) NULL,
    ///         -- from main
    ///     DOB  SMALLDATETIME NULL,
    ///         -- from main
    /// This rejoins them onto the preceding line:
    ///     Name NVARCHAR(500) NULL, -- from main
    ///     DOB  SMALLDATETIME NULL, -- from main
    ///
    /// **Which comments to rejoin cannot be decided from this text, and that is why
    /// <paramref name="trailingComments"/> exists.** ScriptDom emits a comment that trailed a column
    /// definition and a comment that was always on its own line above one *identically* — the two inputs
    /// produce the same output, byte for byte. So a pass that rejoins whatever it finds also collapses
    /// comments the author deliberately put on their own line, and a block of them onto a single line:
    /// three `-- REMOVED: LEFT JOIN …` notes documenting removed joins came back concatenated onto the
    /// end of the FROM line, where they can no longer be un-commented one at a time. Nothing is disabled
    /// by it — a comment can only ever be appended to code, never the reverse — but it is not what was
    /// written, and the formatter does not get to rewrite prose.
    ///
    /// So the decision is taken from the *source*: only a comment whose text the source carried at the end
    /// of a line of code is rejoined, and only as many times as the source did that. Anything else stays
    /// where ScriptDom put it. Duplicate texts are matched by count rather than identity, so the two
    /// `-- from main` comments above are both rejoined; where the same text appears both trailing and on
    /// its own line the credit may be spent on the wrong one, which costs a comment one line of position
    /// and nothing else.
    ///
    /// The same knowledge fixes the mirror case, which is the other half of the same complaint:
    /// **ScriptDom moves an own-line comment up onto the end of the preceding code line.** A comment above
    /// a JOIN comes back as `FROM dbo.A AS a -- keep this join`. So a comment that is trailing here but was
    /// on its own line in the source is pushed back onto its own line. That is why the three `-- REMOVED`
    /// notes above survive as three lines rather than one: not rejoining accounts for two of them, and this
    /// accounts for the first. Moving a comment between its own line and the end of the line above it can
    /// never change what the code does, which is what makes the round trip safe in both directions.
    /// </summary>
    private static string RestoreCommentLinePlacement(string sql, IDictionary<string, int> trailingComments)
    {
        // An *empty* set is meaningful — it says the source had no trailing comments at all, so every
        // comment ScriptDom left trailing is one it relocated and every one of them gets pushed back down.
        // Only a null set (placement unknown) means leave the layout alone.
        if (trailingComments == null)
            return sql;

        // Copied because the credits are consumed as they are spent, and Apply's caller keeps its own.
        var remaining = new Dictionary<string, int>(trailingComments, StringComparer.Ordinal);

        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string currentStripped = line.TrimStart();

            // Skip blank lines and comment-only lines — they stay as-is
            if (string.IsNullOrWhiteSpace(currentStripped) || currentStripped.StartsWith("--"))
            {
                result.Add(line);
                continue;
            }

            int currentIndent = line.Length - currentStripped.Length;
            string leadingWhitespace = line.Substring(0, currentIndent);

            // Does this code line carry a comment the source had on its own line? If so ScriptDom pulled it
            // up here, and it goes back down. A line that has just shed a comment does not then adopt the
            // next one — it would only be trading one relocation for another.
            string splitOff = null;
            int commentStart = FindLineCommentStart(line.TrimEnd());
            if (commentStart >= 0)
            {
                string trailingText = line.TrimEnd().Substring(commentStart).TrimEnd();
                if (trailingText.Length > 2 && !TakeTrailingCredit(remaining, trailingText))
                {
                    splitOff = trailingText;
                    line = line.Substring(0, commentStart).TrimEnd();
                }
            }

            // This line has code. Merge any immediately following comment lines onto it,
            // as long as they're at the same or deeper indent (ScriptDom pattern).
            string merged = line.TrimEnd();

            while (splitOff == null && i + 1 < lines.Length)
            {
                string nextStripped = lines[i + 1].TrimStart();
                int nextIndent = lines[i + 1].Length - nextStripped.Length;
                string commentText = nextStripped.TrimEnd();

                // Merge comment onto preceding code line if:
                // - it's a comment at the same or deeper indent
                // - it has actual comment text (not just "--" as a section divider)
                // - the source spelled this comment as a trailing one, and has a credit left for it
                if (nextStripped.StartsWith("--") &&
                    commentText.Length > 2 &&
                    nextIndent >= currentIndent &&
                    TakeTrailingCredit(remaining, commentText))
                {
                    merged += " " + nextStripped;
                    i++;
                }
                else
                {
                    break;
                }
            }

            result.Add(merged);
            if (splitOff != null)
            {
                // Where the comment heads a block of them, match the rest of the block rather than the code
                // line it was pulled off — otherwise the first of three "-- REMOVED" notes sits a level out
                // from its two siblings.
                string indent = leadingWhitespace;
                if (i + 1 < lines.Length)
                {
                    string nextStripped = lines[i + 1].TrimStart();
                    if (nextStripped.StartsWith("--"))
                        indent = lines[i + 1].Substring(0, lines[i + 1].Length - nextStripped.Length);
                }

                result.Add(indent + splitOff);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    private static bool TakeTrailingCredit(Dictionary<string, int> remaining, string commentText)
    {
        if (!remaining.TryGetValue(commentText, out int count) || count <= 0)
            return false;

        remaining[commentText] = count - 1;
        return true;
    }

    private static string ApplyLeadingCommas(string sql, FormatterOptions options)
    {
        // Convert trailing commas to leading commas in multi-line lists.
        // Handles inline comments: "col1, -- comment" and standalone comment lines after commas.
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        // The shallowest code line since the last comma this pass moved — i.e. the indent the current list
        // item started at. A multi-line item ends deeper than it began (a reflowed CASE ends on its END, a
        // wrapped predicate on its last AND), and comparing the next item against that closing line alone
        // read the list as no longer being a list: the comma stayed stranded at the end of the deep line
        // while the item below it began with none. See the guard below.
        int itemIndent = int.MaxValue;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd();
            string leading = line.TrimStart();
            int currentIndent = line.Length - leading.Length;

            if (leading.Length > 0 && !leading.StartsWith("--"))
                itemIndent = Math.Min(itemIndent, currentIndent);

            // Check for trailing comma (possibly before an inline comment)
            // Pattern 1: line ends with ","
            // Pattern 2: line has ", -- comment" (comma before inline comment)
            string commaLine = null;
            bool hasInlineComment = false;
            string inlineComment = null;

            if (trimmed.EndsWith(","))
            {
                commaLine = trimmed.Substring(0, trimmed.Length - 1);
            }
            else
            {
                // Check for ", -- comment" pattern
                var commentMatch = Regex.Match(trimmed, @"^(.+),\s*(--.*?)$");
                if (commentMatch.Success)
                {
                    commaLine = commentMatch.Groups[1].Value;
                    hasInlineComment = true;
                    inlineComment = commentMatch.Groups[2].Value;
                }
            }

            if (commaLine != null && i + 1 < lines.Length)
            {
                // Find the next non-comment content line to receive the leading comma
                int targetIdx = i + 1;
                var commentLines = new List<int>();

                while (targetIdx < lines.Length)
                {
                    string targetTrimmed = lines[targetIdx].TrimStart();
                    if (string.IsNullOrWhiteSpace(targetTrimmed))
                        break;
                    if (targetTrimmed.StartsWith("--"))
                    {
                        commentLines.Add(targetIdx);
                        targetIdx++;
                        continue;
                    }
                    break;
                }

                if (targetIdx < lines.Length && !string.IsNullOrWhiteSpace(lines[targetIdx].TrimStart()))
                {
                    string targetLine = lines[targetIdx];
                    string targetTrimmed = targetLine.TrimStart();
                    int targetIndent = targetLine.Length - targetTrimmed.Length;

                    // The comma belongs to the next item when that item is no further left than the one
                    // being closed started — not merely no further left than the line that closed it.
                    if (targetIndent >= Math.Min(currentIndent, itemIndent))
                    {
                        // Emit current line without comma, with inline comment if present
                        if (hasInlineComment)
                            result.Add(commaLine + " " + inlineComment);
                        else
                            result.Add(commaLine);

                        // Emit any intervening comment lines as-is
                        for (int c = i + 1; c < targetIdx; c++)
                            result.Add(lines[c]);

                        // Prepend comma to target line, positioned 2 spaces back for alignment
                        string leadingWhitespace = targetLine.Substring(0, targetIndent);
                        lines[targetIdx] = PrependLeadingComma(leadingWhitespace, targetTrimmed, options);

                        itemIndent = targetIndent;
                        i = targetIdx - 1; // loop will increment to targetIdx
                        continue;
                    }
                }
            }

            result.Add(line);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// Prepends ", " to a line. By default the comma is positioned back from the column indent
    /// so that column names stay aligned. When LeadingCommaKeepIndent is set, the full indent is
    /// preserved and the comma sits at the item's own indent level ("\t, [Name]").
    /// </summary>
    private static string PrependLeadingComma(string leadingWhitespace, string content, FormatterOptions options)
    {
        if (options.LeadingCommaKeepIndent)
            return leadingWhitespace + ", " + content;

        if (leadingWhitespace.Length >= 2 && !leadingWhitespace.Contains('\t'))
        {
            return leadingWhitespace.Substring(0, leadingWhitespace.Length - 2) + ", " + content;
        }
        else if (leadingWhitespace.Contains('\t'))
        {
            int lastTab = leadingWhitespace.LastIndexOf('\t');
            return leadingWhitespace.Substring(0, lastTab) + ", " + content;
        }
        else
        {
            return ", " + content;
        }
    }

    /// <summary>
    /// Applies the alias style. Note that <see cref="AliasStyleOption.AS"/> is deliberately a no-op:
    /// the AST records only that an alias exists, not whether the source spelled it with AS, so
    /// ScriptDom's generator has already emitted "AS" before every table and column alias by the time
    /// we get here. There is nothing left to add, and the pass that used to try was a bare
    /// "identifier identifier" regex over the whole script — which matched far more than aliases:
    /// "SET ANSI_NULLS ON" became "SET AS ANSI_NULLS ON", "IS NULL" became "IS AS NULL", and even
    /// comment prose ("-- Author: Alex Rivera") was rewritten. Adding AS is ScriptDom's job; don't
    /// reintroduce a text pass for it.
    /// </summary>
    private static string ApplyAliasStyle(string sql, FormatterOptions options)
    {
        switch (options.AliasStyle)
        {
            case AliasStyleOption.NoAS:
                return RemoveAliasAs(sql);
            case AliasStyleOption.ColumnEquals:
                return ApplyColumnEqualsStyle(sql);
            default: // AS (already emitted by ScriptDom) and Unchanged
                return sql;
        }
    }

    /// <summary>
    /// Drops the AS keyword from aliases. Only the two positions an alias can actually occupy are
    /// considered — a SELECT-list item ("expr AS alias") and a table reference ("dbo.T AS t"). That
    /// whitelist is the whole point: every other AS in a script has to survive, and a blanket
    /// "AS &lt;word&gt;" replace silently mangles CAST(x AS INT), CREATE PROC ... AS, DECLARE @x AS INT,
    /// CREATE TYPE x AS TABLE, EXECUTE AS OWNER and XMLNAMESPACES('u' AS ns) into SQL that no longer
    /// parses.
    /// </summary>
    private static string RemoveAliasAs(string sql) => RemoveTableAliasAs(RemoveColumnAliasAs(sql));

    /// <summary>
    /// Converts SELECT column aliases from "expr AS Alias" to "Alias = expr".
    /// Only acts within SELECT lists (between SELECT and the next top-level clause keyword),
    /// so table aliases in FROM/JOIN are untouched. Parenthesis/string/comment aware, so a
    /// column whose expression spans multiple lines (e.g. a multi-line scalar subquery) is
    /// handled, and nested subquery SELECT lists are transformed recursively.
    /// </summary>
    private static string ApplyColumnEqualsStyle(string sql) => TransformSelectLists(sql, TransformColumnItem);

    /// <summary>Removes AS from SELECT-list aliases ("expr AS alias" -&gt; "expr alias").</summary>
    private static string RemoveColumnAliasAs(string sql) => TransformSelectLists(sql, StripColumnItemAs);

    /// <summary>
    /// Walks every SELECT list in the script and rewrites its top-level column items with
    /// <paramref name="itemTransform"/>, leaving everything outside the lists byte-for-byte alone.
    /// Nested subquery lists are reached by the transform recursing, not by this loop — the outer
    /// walk resumes past the whole body it just handed over.
    /// </summary>
    private static string TransformSelectLists(string sql, Func<string, string> itemTransform)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var sb = new StringBuilder();
        int i = 0, n = sql.Length;

        while (i < n)
        {
            int selIdx = FindKeyword(sql, i, "SELECT");
            if (selIdx < 0)
            {
                sb.Append(sql, i, n - i);
                break;
            }

            int bodyStart = ConsumeSelectModifiers(sql, selIdx + 6); // 6 = "SELECT".Length
            int bodyEnd = FindSelectBodyEnd(sql, bodyStart, n);

            sb.Append(sql, i, bodyStart - i);          // text up to and including SELECT + modifiers
            var items = SplitTopLevelCommaItems(sql.Substring(bodyStart, bodyEnd - bodyStart));
            for (int k = 0; k < items.Count; k++)
                items[k] = itemTransform(items[k]);
            sb.Append(string.Join(",", items));
            i = bodyEnd;
        }

        return sb.ToString();
    }

    /// <summary>Splits a SELECT-list item into its leading whitespace, its content, and its trailing
    /// whitespace, so a rewrite of the content preserves the item's comma/line layout exactly.</summary>
    private static void SplitItemPadding(string item, out string lead, out string core, out string trail)
    {
        int ls = 0;
        while (ls < item.Length && char.IsWhiteSpace(item[ls])) ls++;
        int te = item.Length;
        while (te > ls && char.IsWhiteSpace(item[te - 1])) te--;

        lead = item.Substring(0, ls);
        core = item.Substring(ls, te - ls);
        trail = item.Substring(te);
    }

    /// <summary>
    /// Rewrites one top-level column item ("expr AS alias" -> "alias = expr"), recursing into the
    /// expression so nested subquery aliases are converted too.
    /// </summary>
    private static string TransformColumnItem(string item)
    {
        SplitItemPadding(item, out string lead, out string core, out string trail);
        if (core.Length == 0)
            return item;

        int asIdx = FindTopLevelAs(core);
        if (asIdx < 0)
            return lead + ApplyColumnEqualsStyle(core) + trail; // no alias — still recurse for nested SELECTs

        string expr = core.Substring(0, asIdx).TrimEnd();
        string aliasPart = core.Substring(asIdx + 2).Trim();

        if (expr.Length == 0 || !IsSimpleAliasName(aliasPart))
            return lead + ApplyColumnEqualsStyle(core) + trail; // not a simple "expr AS alias" — leave it

        string comments = LiftLeadingComments(ref lead, ref expr);
        return lead + comments + AliasAsAssignmentTarget(aliasPart) + " = " + ApplyColumnEqualsStyle(expr) + trail;
    }

    /// <summary>
    /// Moves the "--" comments an item begins with out in front of the rewrite, and hands them back for the
    /// caller to emit there.
    ///
    /// This is the only pass that writes something to the *head* of an item, and a comment sitting there
    /// swallows it: the alias lands on the comment's line ("'Ongoing Qty' = -- CHANGE 2: …") and the
    /// expression it names starts on the line below. Still legal, still parses, and unreadable.
    ///
    /// They come back at the indent of the item's code rather than the one they arrived with, because
    /// ScriptDom parks a comment at whatever column the previous line ended in — after a run-on CASE that
    /// is a couple of hundred characters out, and once the CASE below it is reflowed the comment is the only
    /// thing left out there.
    /// </summary>
    private static string LiftLeadingComments(ref string lead, ref string expr)
    {
        if (!expr.StartsWith("--"))
            return "";

        var comments = new List<string>();
        string rest = expr, indent = "";

        while (rest.StartsWith("--"))
        {
            int nl = rest.IndexOf('\n');
            if (nl < 0)
                return "";                                  // a comment with no code after it — not this pass's business

            comments.Add(rest.Substring(0, nl).TrimEnd());
            rest = rest.Substring(nl + 1);

            int ws = 0;
            while (ws < rest.Length && (rest[ws] == ' ' || rest[ws] == '\t')) ws++;
            indent = rest.Substring(0, ws);
            rest = rest.Substring(ws);
        }

        if (rest.Length == 0)
            return "";

        expr = rest;

        int lastNewline = lead.LastIndexOf('\n');
        if (lastNewline >= 0)
            lead = lead.Substring(0, lastNewline + 1) + indent;

        var sb = new StringBuilder();
        foreach (var comment in comments)
            sb.Append(comment).Append(Environment.NewLine).Append(indent);
        return sb.ToString();
    }

    /// <summary>
    /// The left-hand side an alias becomes in "alias = expression" form.
    ///
    /// **Every spelling an alias can arrive in belongs here, not just the bare and bracketed ones.** A
    /// warehouse SELECT list is mostly <c>AS 'Ongoing Qty'</c> and <c>AS #Ongoing</c>, and while those were
    /// skipped the option looked like it only worked on half a query — every AS the user was watching for
    /// stayed exactly where it was. All four forms are legal to the left of the "=", which
    /// <c>AliasStyleTests</c> pins by re-parsing.
    ///
    /// **The spelling is otherwise carried across untouched**, and the one case that is not — a bracketed
    /// name that needs no brackets, which reads better bare — is the one that cannot change what the column
    /// is called. Re-quoting <c>'Split ship'</c> as <c>[Split ship]</c> can: bracketed names are the only
    /// thing <see cref="ApplyIdentifierCase"/> touches, so under IdentifierCase = Upper that alias would come
    /// back as <c>[SPLIT SHIP]</c> and the result set's column heading would change. Choosing the alias style
    /// must not rename a column.
    /// </summary>
    private static string AliasAsAssignmentTarget(string alias)
    {
        if (alias[0] != '[')
            return alias;

        string inner = alias.Substring(1, alias.Length - 2);
        return Regex.IsMatch(inner, @"^[A-Za-z_][A-Za-z0-9_]*$") ? inner : alias;
    }

    /// <summary>
    /// Drops the AS from one top-level column item ("expr AS alias" -> "expr alias"), recursing into
    /// the expression so nested subquery lists are handled too. Anything that isn't a plain
    /// "expression AS simple-name" — an alias carrying a trailing comment, say — is left as it is.
    /// </summary>
    private static string StripColumnItemAs(string item)
    {
        SplitItemPadding(item, out string lead, out string core, out string trail);
        if (core.Length == 0)
            return item;

        int asIdx = FindTopLevelAs(core);
        if (asIdx < 0)
            return lead + RemoveColumnAliasAs(core) + trail;

        string expr = core.Substring(0, asIdx).TrimEnd();
        string aliasPart = core.Substring(asIdx + 2).Trim();

        if (expr.Length == 0 || !IsSimpleAliasName(aliasPart))
            return lead + RemoveColumnAliasAs(core) + trail;

        return lead + RemoveColumnAliasAs(expr) + " " + aliasPart + trail;
    }

    /// <summary>A bare, bracketed or quoted name — the only things that may follow AS in an alias.
    /// Quoted forms are included because `SELECT x 'Total'` and `FROM t "a"` are both legal, and the bare
    /// form allows "#" and "$" because a regular T-SQL identifier may start with either ("AS #Ongoing" is
    /// how a hash-prefixed report column arrives).</summary>
    private static bool IsSimpleAliasName(string s) =>
        s.Length > 0 && Regex.IsMatch(s, @"^(\[[^\]]*(?:\]\][^\]]*)*\]|""[^""]+""|'[^']+'|[\w#$]+)$");

    /// <summary>Keywords after which a table reference — and therefore an alias — may appear.</summary>
    private static readonly Regex TableRefIntroKeyword = new Regex(
        @"\G(FROM|JOIN|APPLY|PIVOT|UNPIVOT|USING|MERGE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Keywords that end a table reference. Hitting one before an AS means the reference had
    /// no alias, and any AS past it belongs to something else entirely.</summary>
    private static readonly Regex TableRefBoundaryKeyword = new Regex(
        @"\G(ON|WHERE|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|FOR|OPTION|WITH|TABLESAMPLE|SELECT|INSERT|UPDATE|DELETE|SET|VALUES|INTO|WHEN|MATCHED|OUTPUT|DECLARE|BEGIN|END|IF|ELSE|WHILE|RETURN|EXEC|EXECUTE|CREATE|ALTER|DROP|TRUNCATE|PRINT|RAISERROR|THROW|GRANT|REVOKE|DENY|GO|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|JOIN|APPLY|PIVOT|UNPIVOT|FROM|USING|MERGE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes AS from table aliases ("FROM dbo.T AS t" -> "FROM dbo.T t"). Each table-reference
    /// keyword starts a scan bounded by the clause's end, and within each comma-separated reference
    /// the first depth-0 AS is the alias by grammar. The walk resumes just past the keyword rather
    /// than past the clause, so a derived table's own FROM is reached on a later iteration.
    /// A reference comma-joined onto the tail of an ON clause ("FROM a JOIN b ON ..., c AS x") keeps
    /// its AS: the ON ended the clause. Leaving an AS in place is the harmless direction to fail, and
    /// mixing ANSI and comma joins in one FROM is not worth widening the scan for.
    /// </summary>
    private static string RemoveTableAliasAs(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return sql;

        var cuts = new List<KeyValuePair<int, int>>();   // half-open [start, end) spans to delete
        var seen = new HashSet<int>();
        int i = 0, n = sql.Length;

        while (i < n)
        {
            char c = sql[i];
            if (c == '\'') { i = SkipSingleQuote(sql, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(sql, i, n); continue; }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-') { i = SkipLineComment(sql, i, n); continue; }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*') { i = SkipBlockComment(sql, i, n); continue; }

            var km = IsWordStart(sql, i) ? TableRefIntroKeyword.Match(sql, i) : Match.Empty;
            if (!km.Success || km.Index != i) { i++; continue; }

            int clauseStart = i + km.Length;
            int clauseEnd = FindTableRefEnd(sql, clauseStart, n);

            foreach (var reference in SplitTopLevelCommaSpans(sql, clauseStart, clauseEnd))
            {
                int asIdx = FindFirstTopLevelAs(sql, reference.Key, reference.Value);
                if (asIdx < 0 || !seen.Add(asIdx))
                    continue;

                int aliasStart = SkipTrivia(sql, asIdx + 2, reference.Value);
                int aliasEnd = ReadAliasToken(sql, aliasStart, reference.Value);
                if (aliasEnd > aliasStart)
                    cuts.Add(new KeyValuePair<int, int>(asIdx, aliasStart)); // drop "AS" and the space after it
            }

            i = clauseStart;
        }

        if (cuts.Count == 0)
            return sql;

        cuts.Sort((a, b) => a.Key.CompareTo(b.Key));
        var sb = new StringBuilder(sql.Length);
        int copied = 0;
        foreach (var cut in cuts)
        {
            if (cut.Key < copied) continue;
            sb.Append(sql, copied, cut.Key - copied);
            copied = cut.Value;
        }
        sb.Append(sql, copied, n - copied);
        return sb.ToString();
    }

    /// <summary>End of a table reference clause: a depth-0 boundary keyword, ";", or the ")" that
    /// closes the enclosing subquery.</summary>
    private static int FindTableRefEnd(string s, int start, int end)
    {
        int depth = 0, i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, end); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, end); continue; }
            if (c == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth == 0) return i; depth--; i++; continue; }
            if (depth == 0)
            {
                if (c == ';') return i;
                if (IsWordStart(s, i))
                {
                    var bm = TableRefBoundaryKeyword.Match(s, i);
                    if (bm.Success && bm.Index == i) return i;
                }
            }
            i++;
        }
        return end;
    }

    /// <summary>Splits [start, end) at depth-0 commas, yielding one half-open span per item.</summary>
    private static List<KeyValuePair<int, int>> SplitTopLevelCommaSpans(string s, int start, int end)
    {
        var spans = new List<KeyValuePair<int, int>>();
        int depth = 0, i = start, from = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, end); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, end); continue; }
            if (c == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }
            if (c == ',' && depth == 0) { spans.Add(new KeyValuePair<int, int>(from, i)); from = i + 1; }
            i++;
        }
        spans.Add(new KeyValuePair<int, int>(from, end));
        return spans;
    }

    /// <summary>Index of the first depth-0 "AS" in [start, end), or -1.</summary>
    private static int FindFirstTopLevelAs(string s, int start, int end)
    {
        int depth = 0, i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, end); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, end); continue; }
            if (c == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }
            if (depth == 0 && (c == 'A' || c == 'a') && IsWordStart(s, i) && MatchWordCI(s, i, "AS"))
                return i;
            i++;
        }
        return -1;
    }

    private static int SkipTrivia(string s, int i, int end)
    {
        while (i < end)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (s[i] == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            break;
        }
        return i;
    }

    /// <summary>End index of the alias name starting at <paramref name="i"/>, or <paramref name="i"/>
    /// if there isn't one.</summary>
    private static int ReadAliasToken(string s, int i, int end)
    {
        if (i >= end) return i;
        if (s[i] == '[') return Math.Min(SkipBracketToken(s, i, end), end);
        if (s[i] == '"' || s[i] == '\'')
        {
            char q = s[i];
            int j = i + 1;
            while (j < end && s[j] != q) j++;
            return (j < end) ? j + 1 : i;
        }
        int k = i;
        while (k < end && (char.IsLetterOrDigit(s[k]) || s[k] == '_' || s[k] == '#' || s[k] == '$')) k++;
        return k;
    }

    /// <summary>Index of the last top-level (depth-0, outside strings/comments) "AS" keyword in an
    /// expression, or -1. Used to locate a column's alias without matching an AS inside CAST(... AS ...).</summary>
    private static int FindTopLevelAs(string s)
    {
        int depth = 0, n = s.Length, found = -1;
        int i = 0;
        while (i < n)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, n); continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-') { i = SkipLineComment(s, i, n); continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipBlockComment(s, i, n); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }
            if (depth == 0 && (c == 'A' || c == 'a') && IsWordStart(s, i) && MatchWordCI(s, i, "AS"))
                found = i;
            i++;
        }
        return found;
    }

    /// <summary>Splits a SELECT-list body at top-level (depth-0) commas, keeping the text between
    /// commas verbatim so the caller can rejoin with "," and reproduce the original layout.</summary>
    private static List<string> SplitTopLevelCommaItems(string s)
    {
        var items = new List<string>();
        int depth = 0, n = s.Length, start = 0, i = 0;
        while (i < n)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, n); continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-') { i = SkipLineComment(s, i, n); continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipBlockComment(s, i, n); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }
            if (c == ',' && depth == 0) { items.Add(s.Substring(start, i - start)); start = i + 1; }
            i++;
        }
        items.Add(s.Substring(start));
        return items;
    }

    /// <summary>Finds the index of the end of a SELECT list: the first top-level clause keyword,
    /// statement terminator, or the ")" that closes an enclosing subquery.</summary>
    private static int FindSelectBodyEnd(string s, int start, int end)
    {
        int depth = 0, i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, end); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, end); continue; }
            if (c == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth == 0) return i; depth--; i++; continue; }
            if (depth == 0)
            {
                if (c == ';') return i;
                if (IsWordStart(s, i))
                {
                    var bm = SelectBoundaryKeyword.Match(s, i);
                    if (bm.Success && bm.Index == i) return i;
                }
            }
            i++;
        }
        return end;
    }

    private static readonly Regex SelectBoundaryKeyword = new Regex(
        @"\G(FROM|WHERE|HAVING|UNION|EXCEPT|INTERSECT|INTO|OPTION|WINDOW|FOR|GROUP\s+BY|ORDER\s+BY)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Consumes optional SELECT modifiers (DISTINCT/ALL, TOP (n) [PERCENT] [WITH TIES]) plus
    /// surrounding whitespace, returning the index where the actual column list begins.</summary>
    private static int ConsumeSelectModifiers(string s, int i)
    {
        var m = SelectModifiers.Match(s, i);
        return (m.Success && m.Index == i) ? i + m.Length : i;
    }

    private static readonly Regex SelectModifiers = new Regex(
        @"\G\s*(?:(?:DISTINCT|ALL)\s+)?(?:TOP\s*\(?\s*\d+\s*\)?\s*(?:PERCENT\s*)?(?:WITH\s+TIES\s*)?)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // --- character-index scanning helpers (string / comment / bracket aware) ---

    private static int FindKeyword(string s, int start, string kw)
    {
        int i = start, n = s.Length;
        char first = char.ToUpperInvariant(kw[0]);
        while (i < n)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, n); continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-') { i = SkipLineComment(s, i, n); continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipBlockComment(s, i, n); continue; }
            if (char.ToUpperInvariant(c) == first && IsWordStart(s, i) && MatchWordCI(s, i, kw))
                return i;
            i++;
        }
        return -1;
    }

    private static bool IsWordStart(string s, int i)
    {
        if (i == 0) return true;
        char p = s[i - 1];
        return !(char.IsLetterOrDigit(p) || p == '_' || p == '@' || p == '#' || p == '$');
    }

    private static bool MatchWordCI(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        int after = i + word.Length;
        if (after < s.Length)
        {
            char a = s[after];
            if (char.IsLetterOrDigit(a) || a == '_' || a == '@' || a == '#' || a == '$') return false;
        }
        return true;
    }

    private static int SkipSingleQuote(string s, int i, int n)
    {
        int j = i + 1;
        while (j < n)
        {
            if (s[j] == '\'')
            {
                if (j + 1 < n && s[j + 1] == '\'') { j += 2; continue; }
                return j + 1;
            }
            j++;
        }
        return n;
    }

    private static int SkipBracketToken(string s, int i, int n)
    {
        int j = i + 1;
        while (j < n)
        {
            if (s[j] == ']')
            {
                if (j + 1 < n && s[j + 1] == ']') { j += 2; continue; } // ]] is an escaped ]
                return j + 1;
            }
            j++;
        }
        return n;
    }

    private static int SkipLineComment(string s, int i, int n)
    {
        int j = i + 2;
        while (j < n && s[j] != '\n') j++;
        return j; // leave the newline for the caller
    }

    private static int SkipBlockComment(string s, int i, int n)
    {
        int j = i + 2, nest = 1;
        while (j + 1 < n && nest > 0)
        {
            if (s[j] == '/' && s[j + 1] == '*') { nest++; j += 2; continue; }
            if (s[j] == '*' && s[j + 1] == '/') { nest--; j += 2; continue; }
            j++;
        }
        return (nest == 0) ? j : n;
    }

    /// <summary>
    /// T-SQL reserved keywords plus common keyword-like column names (Name, Type, Source, Date, ...).
    /// Used by RemoveBrackets to decide which bracketed identifiers must keep their brackets.
    /// Keeping brackets is always safe, so this list errs toward inclusion.
    /// </summary>
    internal static readonly HashSet<string> ReservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // SQL Server reserved keywords
        "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN",
        "BETWEEN", "BREAK", "BROWSE", "BULK", "BY", "CASCADE", "CASE", "CHECK", "CHECKPOINT",
        "CLOSE", "CLUSTERED", "COALESCE", "COLLATE", "COLUMN", "COMMIT", "COMPUTE", "CONSTRAINT",
        "CONTAINS", "CONTAINSTABLE", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CURRENT",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR", "DATABASE",
        "DBCC", "DEALLOCATE", "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC", "DISK", "DISTINCT",
        "DISTRIBUTED", "DOUBLE", "DROP", "DUMP", "ELSE", "END", "ERRLVL", "ESCAPE", "EXCEPT",
        "EXEC", "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR", "FOR",
        "FOREIGN", "FREETEXT", "FREETEXTTABLE", "FROM", "FULL", "FUNCTION", "GOTO", "GRANT",
        "GROUP", "HAVING", "HOLDLOCK", "IDENTITY", "IDENTITY_INSERT", "IDENTITYCOL", "IF", "IN",
        "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "KILL", "LEFT",
        "LIKE", "LINENO", "LOAD", "MERGE", "NATIONAL", "NOCHECK", "NONCLUSTERED", "NOT", "NULL",
        "NULLIF", "OF", "OFF", "OFFSETS", "ON", "OPEN", "OPENDATASOURCE", "OPENQUERY",
        "OPENROWSET", "OPENXML", "OPTION", "OR", "ORDER", "OUTER", "OVER", "PERCENT", "PIVOT",
        "PLAN", "PRECISION", "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC", "RAISERROR",
        "READ", "READTEXT", "RECONFIGURE", "REFERENCES", "REPLICATION", "RESTORE", "RESTRICT",
        "RETURN", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROWCOUNT", "ROWGUIDCOL", "RULE",
        "SAVE", "SCHEMA", "SELECT", "SESSION_USER", "SET", "SETUSER", "SHUTDOWN", "SOME",
        "STATISTICS", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "TEXTSIZE", "THEN", "TO", "TOP",
        "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TSEQUAL", "UNION", "UNIQUE", "UNPIVOT",
        "UPDATE", "UPDATETEXT", "USE", "USER", "VALUES", "VARYING", "VIEW", "WAITFOR", "WHEN",
        "WHERE", "WHILE", "WITH", "WRITETEXT",
        // Common keyword-like column names that read best bracketed
        "NAME", "TYPE", "SOURCE", "DATE", "TIME", "DATETIME", "TIMESTAMP", "STATUS", "VALUE",
        "LEVEL", "STATE", "LANGUAGE", "TARGET", "POSITION", "LABEL", "COMMENT", "TEXT",
    };

    private static string ApplyBracketQuoting(string sql, FormatterOptions options)
    {
        if (options.BracketQuoting == BracketQuotingOption.Unchanged)
            return sql;

        if (options.BracketQuoting == BracketQuotingOption.RemoveBrackets)
        {
            // Remove brackets from identifiers that don't need them.
            // Keep brackets on reserved words and identifiers with spaces/special chars.
            var reservedWords = ReservedKeywords;

            sql = Regex.Replace(sql, @"\[([^\]]+)\]", m =>
            {
                string identifier = m.Groups[1].Value;
                // Keep brackets if identifier is a reserved word, has spaces, or has special chars
                if (reservedWords.Contains(identifier) ||
                    identifier.Contains(" ") ||
                    Regex.IsMatch(identifier, @"[^a-zA-Z0-9_#@]"))
                {
                    return m.Value;
                }
                return identifier;
            });
        }
        else if (options.BracketQuoting == BracketQuotingOption.AddBrackets)
        {
            // Add brackets to all identifiers — this is complex and best done at the AST level.
            // For post-processing, we bracket identifiers in common patterns.
            // Match multi-part names like dbo.TableName or column references
            sql = Regex.Replace(sql, @"(?<=(?:FROM|JOIN|INTO|UPDATE|TABLE|VIEW|INDEX\s+ON)\s+)(\w+)\.(\w+)(?:\.(\w+))?",
                m =>
                {
                    if (m.Groups[3].Success)
                        return $"[{m.Groups[1].Value}].[{m.Groups[2].Value}].[{m.Groups[3].Value}]";
                    return $"[{m.Groups[1].Value}].[{m.Groups[2].Value}]";
                }, RegexOptions.IgnoreCase);
        }

        return sql;
    }

    /// <summary>
    /// ScriptDom splits JOINs across 3 lines:
    ///     INNER JOIN
    ///     dbo.Orders AS o
    ///     ON ...
    /// This collapses the JOIN keyword and table onto one line, and indents ON beneath:
    ///     INNER JOIN dbo.Orders AS o
    ///         ON ...
    /// </summary>
    private static string CollapseJoinLayout(string sql, FormatterOptions options)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string indent = options.IndentStyle == IndentStyleOption.Tabs
            ? new string('\t', 1)
            : new string(' ', options.IndentSize);

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd();
            string stripped = trimmed.TrimStart();

            // Detect a line that is ONLY a JOIN keyword (e.g. "       INNER JOIN" or "     LEFT OUTER JOIN")
            if (Regex.IsMatch(stripped, @"^(INNER\s+JOIN|LEFT\s+(OUTER\s+)?JOIN|RIGHT\s+(OUTER\s+)?JOIN|FULL\s+(OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN)\s*$", RegexOptions.IgnoreCase))
            {
                // Next line should be the table name — merge it onto this line
                if (i + 1 < lines.Length)
                {
                    string nextStripped = lines[i + 1].TrimStart();
                    if (!string.IsNullOrWhiteSpace(nextStripped))
                    {
                        // Get the indentation of the JOIN line
                        string lineIndent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);

                        // Merged: "INNER JOIN dbo.Orders AS o"
                        string merged = lineIndent + stripped + " " + nextStripped;
                        result.Add(merged);
                        i++; // Skip the table name line

                        // Now check if the NEXT line is ON — indent it under the JOIN
                        if (i + 1 < lines.Length)
                        {
                            string onStripped = lines[i + 1].TrimStart();
                            if (onStripped.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ||
                                onStripped.StartsWith("ON\t", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Add(lineIndent + indent + onStripped);
                                i++; // Skip the ON line
                            }
                        }

                        continue;
                    }
                }
            }

            result.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>Anchored JOIN keyword at the start of a line, followed by its table reference.</summary>
    private static readonly Regex JoinLineStart = new Regex(
        @"^(INNER\s+JOIN|LEFT\s+(OUTER\s+)?JOIN|RIGHT\s+(OUTER\s+)?JOIN|FULL\s+(OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN)\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The same table-reference keywords plus CROSS/OUTER APPLY, for the alignment pass only.
    ///
    /// <para>APPLY is deliberately absent from <see cref="JoinLineStart"/>: that one drives
    /// <see cref="ApplyJoinOnSameLine"/>, where matching marks the line as *awaiting an ON*, and an APPLY
    /// has no ON to await. Sharing one regex would leave a stale flag for the next ON at that depth to be
    /// merged onto the wrong line.</para>
    ///
    /// <para>ScriptDom did not start a line with APPLY until 180.107.0 — it kept it on the FROM line
    /// (<c>FROM A AS a CROSS APPLY (SELECT …</c>), which is why the alignment pass never needed it. It now
    /// breaks APPLY onto its own line and indents it to the column after <c>FROM </c> — five spaces, not an
    /// indent unit — so without this the keyword sits on a column no setting here chose and a stacked
    /// derived table hangs off it.</para>
    /// </summary>
    private static readonly Regex JoinOrApplyLineStart = new Regex(
        @"^(INNER\s+JOIN|LEFT\s+(OUTER\s+)?JOIN|RIGHT\s+(OUTER\s+)?JOIN|FULL\s+(OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN|CROSS\s+APPLY|OUTER\s+APPLY)\s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Clause keywords that end a JOIN's table reference, so an ON past one belongs to
    /// something else (or to nothing).</summary>
    private static readonly Regex JoinBlockBoundary = new Regex(
        @"^(WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|EXCEPT|INTERSECT|SELECT|INSERT|UPDATE|DELETE|MERGE|FROM|OPTION|GO)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Merges an ON clause onto the end of its JOIN's table reference.
    ///
    /// **The ON is not necessarily on the line after the JOIN.** A derived table spans as many lines as
    /// its body needs, and the line the ON has to join is the one carrying the closing ")" and the alias
    /// — "…WHERE TerminationDate IS NULL) AS CC". Testing only "is the previous line a JOIN line" is why
    /// every join to a subquery kept its ON on a line of its own while every join to a plain table got it
    /// merged. So a JOIN is remembered as *awaiting* its ON, and the ON is merged onto whatever line
    /// precedes it once it arrives.
    ///
    /// Paren depth is what keeps that honest, and it is tracked **per level**: a derived table's own JOINs
    /// are a query of their own, so an inner ON must pair with the inner JOIN and must never satisfy the
    /// outer one that is still waiting. A clause keyword at the same depth cancels the wait — a JOIN
    /// whose ON never appears (CROSS JOIN, or a comma join) must not adopt the next ON it happens to see.
    /// </summary>
    private static string ApplyJoinOnSameLine(string sql)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        var awaitingOn = new Dictionary<int, bool>();
        int depth = 0;

        foreach (var line in lines)
        {
            string stripped = line.TrimStart();
            int lineDepth = depth;                       // depth as the line begins
            depth = Math.Max(0, depth + ParenDelta(line));

            bool pending;
            if (stripped.StartsWith("ON ", StringComparison.OrdinalIgnoreCase) ||
                stripped.StartsWith("ON\t", StringComparison.OrdinalIgnoreCase))
            {
                if (awaitingOn.TryGetValue(lineDepth, out pending) && pending && result.Count > 0 &&
                    CanAppendTo(result[result.Count - 1]))
                {
                    awaitingOn[lineDepth] = false;
                    result[result.Count - 1] = result[result.Count - 1].TrimEnd() + " " + stripped;
                    continue;
                }
            }
            else if (JoinLineStart.IsMatch(stripped))
            {
                awaitingOn[lineDepth] = true;
            }
            else if (JoinBlockBoundary.IsMatch(stripped))
            {
                awaitingOn[lineDepth] = false;
            }

            result.Add(line);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>False when appending to this line would land the text inside a line comment — the one way
    /// a merge here turns working SQL into SQL that is missing a clause.</summary>
    private static bool CanAppendTo(string line) =>
        !string.IsNullOrWhiteSpace(line) && FindLineCommentStart(line.TrimEnd()) < 0;

    /// <summary>
    /// Normalizes JOIN keywords to the shortest explicit form:
    /// "LEFT OUTER JOIN" -> "LEFT JOIN", "RIGHT OUTER JOIN" -> "RIGHT JOIN".
    /// "FULL OUTER JOIN" is left intact. Case of the surrounding tokens is preserved.
    /// </summary>
    private static string ApplyNormalizeJoinKeywords(string sql)
    {
        return Regex.Replace(sql, @"\b(LEFT|RIGHT)\s+OUTER\s+(JOIN)\b", "$1 $2", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Left-aligns AND/OR continuation lines in a WHERE clause with the WHERE keyword itself,
    /// producing the "river" style:
    ///     WHERE a = 1
    ///     AND b = 2
    /// instead of ScriptDom's indented-under-predicate layout. Multi-line predicates
    /// (lines that don't begin with AND/OR) are left untouched.
    /// </summary>
    private static string ApplyWhereConditionAlignment(string sql)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string whereIndent = null;

        foreach (var line in lines)
        {
            string stripped = line.TrimStart();

            if (Regex.IsMatch(stripped, @"^WHERE\b", RegexOptions.IgnoreCase))
            {
                whereIndent = line.Substring(0, line.Length - stripped.Length);
                result.Add(line);
                continue;
            }

            if (whereIndent != null)
            {
                if (Regex.IsMatch(stripped, @"^(AND|OR)\b", RegexOptions.IgnoreCase))
                {
                    result.Add(whereIndent + stripped.TrimEnd());
                    continue;
                }

                // A new clause keyword or a closing paren ends the WHERE block.
                if (stripped.StartsWith(")") ||
                    Regex.IsMatch(stripped, @"^(FROM|WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|EXCEPT|INTERSECT|SELECT|INSERT|UPDATE|DELETE|MERGE|OPTION|GO)\b", RegexOptions.IgnoreCase))
                {
                    whereIndent = null;
                }
            }

            result.Add(line);
        }

        return string.Join(Environment.NewLine, result);
    }

    private static string ApplyIdentifierCase(string sql, FormatterOptions options)
    {
        if (options.IdentifierCase == CasingOption.Unchanged)
            return sql;

        // Identifier casing is complex because we need to distinguish identifiers from keywords.
        // ScriptGenerator already handles keyword casing, so we mainly target quoted identifiers
        // and unquoted identifiers that aren't keywords.
        // For safety, only apply to bracketed identifiers since those are unambiguously identifiers.
        if (options.IdentifierCase == CasingOption.Lower)
        {
            sql = Regex.Replace(sql, @"\[([^\]]+)\]", m => $"[{m.Groups[1].Value.ToLower()}]");
        }
        else if (options.IdentifierCase == CasingOption.Upper)
        {
            sql = Regex.Replace(sql, @"\[([^\]]+)\]", m => $"[{m.Groups[1].Value.ToUpper()}]");
        }

        return sql;
    }

    /// <summary>
    /// The built-in function names, indexed case-insensitively onto their canonical (upper) spelling.
    /// Taken from the IntelliSense catalog rather than duplicated here — one list means a function
    /// added for completion is cased by the formatter too, and the two can't drift into disagreeing
    /// about what "built-in" means. Niladic entries (CURRENT_TIMESTAMP, SESSION_USER) are excluded:
    /// they take no parentheses, so the call-site test below could never reach them, and they are
    /// reserved keywords that ScriptDom has already cased.
    /// </summary>
    private static readonly Dictionary<string, string> BuiltInFunctionNames = BuildBuiltInFunctionNames();

    private static Dictionary<string, string> BuildBuiltInFunctionNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fn in IntelliSense.SqlBuiltInFunctions.All)
        {
            if (fn.RequiresParentheses)
                names[fn.Name] = fn.Name;
        }
        return names;
    }

    /// <summary>
    /// Cases built-in function names. ScriptDom's KeywordCasing reaches reserved keywords only — a
    /// function call is an identifier in the AST and is regenerated exactly as it was typed, which is
    /// why "select row_number() over (...)" comes back as "SELECT row_number() OVER (...)".
    ///
    /// A name is re-cased only where it is a *call*: immediately followed by "(" and not preceded by a
    /// ".". Those two guards are the whole safety of this pass, because most of these names are also
    /// perfectly good column names. "o.Count" and "s.Value" are qualified, "[Left]" is bracketed and
    /// skipped by the scanner, and a bare "Status" is never followed by "(" — only the call position
    /// tells a function from an identifier that shares its spelling. The dot test is what leaves
    /// "dbo.Count(@x)" — a user function named after a built-in — alone.
    ///
    /// Strings, comments and bracketed identifiers are skipped with the same scanners the alias passes
    /// use; a bare Regex.Replace over the script would case the word "sum" inside a comment.
    /// </summary>
    internal static string ApplyBuiltInFunctionCase(string sql, FormatterOptions options)
    {
        if (string.IsNullOrEmpty(sql) || options.BuiltInFunctionCase == CasingOption.Unchanged)
            return sql;

        bool upper = options.BuiltInFunctionCase == CasingOption.Upper;
        StringBuilder sb = null;
        int i = 0, n = sql.Length, copied = 0;

        while (i < n)
        {
            char c = sql[i];
            if (c == '\'') { i = SkipSingleQuote(sql, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(sql, i, n); continue; }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-') { i = SkipLineComment(sql, i, n); continue; }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*') { i = SkipBlockComment(sql, i, n); continue; }

            if (!(char.IsLetter(c) || c == '_') || !IsWordStart(sql, i)) { i++; continue; }

            int end = i;
            while (end < n && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;

            string canonical;
            if (IsCallSite(sql, i, end, n) && !IsQualified(sql, i) &&
                BuiltInFunctionNames.TryGetValue(sql.Substring(i, end - i), out canonical))
            {
                string replacement = upper ? canonical : canonical.ToLowerInvariant();
                if (string.CompareOrdinal(sql, i, replacement, 0, replacement.Length) != 0 || end - i != replacement.Length)
                {
                    if (sb == null) sb = new StringBuilder(sql.Length);
                    sb.Append(sql, copied, i - copied);
                    sb.Append(replacement);
                    copied = end;
                }
            }

            i = end;
        }

        if (sb == null)
            return sql;

        sb.Append(sql, copied, n - copied);
        return sb.ToString();
    }

    /// <summary>True when the next non-whitespace character after [start, end) is "(".</summary>
    private static bool IsCallSite(string s, int start, int end, int n)
    {
        int j = end;
        while (j < n && char.IsWhiteSpace(s[j])) j++;
        return j < n && s[j] == '(';
    }

    /// <summary>True when the word starting at <paramref name="start"/> is preceded by a "." — i.e. it
    /// is the last part of a qualified name (dbo.Count) rather than a bare built-in call.</summary>
    private static bool IsQualified(string s, int start)
    {
        int j = start - 1;
        while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
        return j >= 0 && s[j] == '.';
    }

    /// <summary>
    /// Left-aligns SET with its UPDATE and pulls the rest of the set clause back by the same amount:
    ///     UPDATE s              UPDATE s
    ///         SET a = 1    ->   SET a = 1
    ///             , b = 2           , b = 2
    ///
    /// ScriptDom's own <c>IndentSetClause = false</c> does the first half, but it also re-flows the
    /// clause to its "river" alignment — SET padded out to the item column — which is a different
    /// layout and follows neither IndentSize nor the tab setting. Shifting the block here keeps every
    /// line's position *relative to the clause*, so a multi-line assignment expression stays where it
    /// was under its item instead of being flattened to one level.
    ///
    /// Runs after ApplyIndentStyle, so indentation is already in the configured units and the shift is
    /// a prefix swap rather than column arithmetic.
    /// </summary>
    private static string ApplySetClauseAlignment(string sql, FormatterOptions options)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        for (int i = 0; i < lines.Length; i++)
        {
            string updateStripped = lines[i].TrimStart();
            if (!Regex.IsMatch(updateStripped, @"^UPDATE\b", RegexOptions.IgnoreCase))
                continue;

            string updateIndent = lines[i].Substring(0, lines[i].Length - updateStripped.Length);

            // The set clause has to be the next non-blank line. "UPDATE t SET a = 1" on one line has
            // nothing to move, and anything else in between is a shape this pass doesn't recognise.
            int s = i + 1;
            while (s < lines.Length && string.IsNullOrWhiteSpace(lines[s])) s++;
            if (s >= lines.Length)
                continue;

            string setStripped = lines[s].TrimStart();
            if (!Regex.IsMatch(setStripped, @"^SET\b", RegexOptions.IgnoreCase))
                continue;

            string setIndent = lines[s].Substring(0, lines[s].Length - setStripped.Length);
            if (setIndent.Length <= updateIndent.Length || !setIndent.StartsWith(updateIndent, StringComparison.Ordinal))
                continue; // already aligned, or an indent shape we can't shift safely

            lines[s] = updateIndent + setStripped;

            // Every following line indented deeper than SET belongs to the clause. The first line at or
            // above SET's level is the next clause (FROM / WHERE / OUTPUT), which must not move.
            for (int k = s + 1; k < lines.Length; k++)
            {
                string bodyStripped = lines[k].TrimStart();
                if (bodyStripped.Length == 0)
                    break;

                string bodyIndent = lines[k].Substring(0, lines[k].Length - bodyStripped.Length);
                if (bodyIndent.Length <= setIndent.Length || !bodyIndent.StartsWith(setIndent, StringComparison.Ordinal))
                    break;

                lines[k] = updateIndent + bodyIndent.Substring(setIndent.Length) + bodyStripped;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ApplyBlankLinesBetweenStatements(string sql, FormatterOptions options)
    {
        if (options.BlankLinesBetweenStatements <= 0)
            return sql;

        // Normalize all existing runs of blank lines to the configured count.
        // ScriptDom uses NumNewlinesAfterStatement to add blank lines between statements,
        // so we just need to ensure every gap has exactly the right number.
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            // If this line is blank and it's part of a gap between content lines, collect the gap
            if (string.IsNullOrWhiteSpace(lines[i]) && result.Count > 0)
            {
                // Skip all consecutive blank lines
                while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                    i++;

                // If there's content after the gap, insert the configured number of blank lines
                if (i < lines.Length)
                {
                    for (int b = 0; b < options.BlankLinesBetweenStatements; b++)
                        result.Add("");
                    result.Add(lines[i]);
                }
                continue;
            }

            result.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, result);
    }

    private static string ApplySemicolons(string sql, FormatterOptions options)
    {
        if (options.TrailingSemicolon == SemicolonOption.Unchanged)
        {
            // Even in Unchanged mode, fix misplaced semicolons after comments
            sql = FixSemicolonAfterComment(sql);
            return sql;
        }

        if (options.TrailingSemicolon == SemicolonOption.Never)
        {
            // Remove trailing semicolons, which may sit either side of an inline comment. Lines that
            // are wholly a comment are skipped for the same reason as FixSemicolonAfterComment: the
            // ";" there is prose, not a terminator.
            var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsWholeLineComment(lines[i]))
                    continue;
                lines[i] = Regex.Replace(lines[i], @";[ \t]*(--.*)?$", "$1");
                lines[i] = Regex.Replace(lines[i], @"(--.*?)\s*;[ \t]*$", "$1");
            }
            return string.Join(Environment.NewLine, lines);
        }

        if (options.TrailingSemicolon == SemicolonOption.Always)
        {
            // ScriptDom adds semicolons, but they may land after comments.
            // Fix any that ended up after a -- comment.
            sql = FixSemicolonAfterComment(sql);
        }

        return sql;
    }

    /// <summary>
    /// Moves semicolons that appear after inline comments to before the comment.
    /// "... code -- comment;" becomes "... code; -- comment"
    /// Finds the first "--" not inside a string literal to locate the comment start.
    ///
    /// A line with no code before the "--" is a whole-line comment and is left alone. Its trailing
    /// ";" is comment prose, not a statement terminator — most often a header's "Example: EXEC
    /// dbo.Foo @Id = 1;" line, which this used to rewrite to a bare ";" followed by the comment.
    /// </summary>
    private static string FixSemicolonAfterComment(string sql)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        foreach (var line in lines)
        {
            string trimmedEnd = line.TrimEnd();

            // Only process lines that end with ";"
            if (!trimmedEnd.EndsWith(";"))
            {
                result.Add(line);
                continue;
            }

            // Find the first "--" that starts a line comment (not inside a string)
            int commentStart = FindLineCommentStart(trimmedEnd);

            if (commentStart >= 0 && commentStart < trimmedEnd.Length - 1)
            {
                // There's a comment, and the ; is after it — move it before
                string codePart = trimmedEnd.Substring(0, commentStart).TrimEnd();
                string commentPart = trimmedEnd.Substring(commentStart).TrimEnd();

                if (codePart.Length == 0)
                {
                    result.Add(line);   // whole-line comment — the ";" is part of the prose
                    continue;
                }


                // Strip the trailing ; from the comment
                if (commentPart.EndsWith(";"))
                    commentPart = commentPart.Substring(0, commentPart.Length - 1).TrimEnd();

                // Add ; to the code part if not already there
                if (!codePart.EndsWith(";"))
                    codePart += ";";

                result.Add(codePart + " " + commentPart);
            }
            else
            {
                result.Add(line);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// Finds the index of the first "--" line comment that's not inside a string literal.
    /// Returns -1 if no comment found.
    /// </summary>
    /// <summary>True when the line carries a "--" comment with no code in front of it. Such a line's
    /// content is prose, and the semicolon passes must not treat a trailing ";" as a terminator.</summary>
    private static bool IsWholeLineComment(string line)
    {
        int commentStart = FindLineCommentStart(line.TrimEnd());
        return commentStart >= 0 && line.Substring(0, commentStart).Trim().Length == 0;
    }

    private static int FindLineCommentStart(string line)
    {
        bool inSingleQuote = false;

        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];

            if (c == '\'')
            {
                // Handle escaped quotes ('')
                if (inSingleQuote && i + 1 < line.Length && line[i + 1] == '\'')
                {
                    i++; // skip escaped quote
                    continue;
                }
                inSingleQuote = !inSingleQuote;
            }
            else if (!inSingleQuote && c == '-' && line[i + 1] == '-')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Aligns FROM and JOIN keywords at the same indentation level.
    /// ScriptDom may indent JOINs deeper than FROM — this flattens them to match.
    ///
    /// **The alignment target is tracked per paren depth, not once for the script.** A derived table
    /// carries a complete query — its own FROM, its own JOINs, its own WHERE — and letting those share
    /// one target is not a near miss: the inner FROM becomes the target for the *outer* query, so every
    /// JOIN after the subquery is indented to wherever that inner FROM happened to sit, and the inner
    /// WHERE cancels the outer FROM before the outer query is finished with it. One subquery in a FROM
    /// clause was enough to misplace every join below it. Keyed by depth, each query aligns to its own
    /// FROM and the nested one still gets aligned rather than merely left alone.
    /// </summary>
    private static string ApplyAlignFromAndJoins(string sql, FormatterOptions options)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string indent = options.IndentStyle == IndentStyleOption.Tabs
            ? new string('\t', 1)
            : new string(' ', options.IndentSize);
        var fromIndentAtDepth = new Dictionary<int, string>();
        var moves = new List<BlockMove>();               // outermost first
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineDepth = depth;                       // depth as the line begins
            depth = Math.Max(0, depth + ParenDelta(lines[i]));

            // A block ends at the first line back at (or above) the depth its opener was on. Its own
            // closing line is still inside it, which is what carries the ")" along with the "(".
            while (moves.Count > 0 && lineDepth <= moves[moves.Count - 1].Depth)
                moves.RemoveAt(moves.Count - 1);

            string line = lines[i];
            foreach (var move in moves)                  // in the order recorded, since each was measured
                line = move.Apply(line);                 // against the line as the previous ones left it

            string stripped = line.TrimStart();
            string lineIndent = line.Substring(0, line.Length - stripped.Length);

            string fromIndent;
            fromIndentAtDepth.TryGetValue(lineDepth, out fromIndent);

            // Detect FROM line and capture its indentation
            if (Regex.IsMatch(stripped, @"^FROM\s", RegexOptions.IgnoreCase))
            {
                fromIndentAtDepth[lineDepth] = lineIndent;
                result.Add(line);
            }

            // Align JOIN and APPLY lines to match FROM indentation
            else if (fromIndent != null && JoinOrApplyLineStart.IsMatch(stripped))
            {
                result.Add(fromIndent + stripped);
                if (depth > lineDepth && !string.Equals(lineIndent, fromIndent, StringComparison.Ordinal) &&
                    BlockIsUnitIndented(lines, i, lineDepth, lineIndent, options, moves))
                {
                    moves.Add(new BlockMove(lineDepth, lineIndent, fromIndent));
                }
            }

            // Indent ON clause one level deeper than FROM
            else if (fromIndent != null && Regex.IsMatch(stripped, @"^ON\s", RegexOptions.IgnoreCase))
            {
                result.Add(fromIndent + indent + stripped);
            }

            else
            {
                // Reset the target when a new major clause ends this query's FROM/JOIN block
                if (fromIndent != null && Regex.IsMatch(stripped, @"^(WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|EXCEPT|INTERSECT|SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase))
                    fromIndentAtDepth.Remove(lineDepth);

                result.Add(line);
            }

            // A closed subquery takes its target with it, so the next one at that depth starts fresh
            // rather than inheriting the previous sibling's FROM.
            if (depth < lineDepth)
            {
                foreach (var stale in fromIndentAtDepth.Keys.Where(d => d > depth).ToList())
                    fromIndentAtDepth.Remove(stale);
            }
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// True when every line of the bracketed block opened on <paramref name="openerIndex"/> is indented as
    /// "<paramref name="baseIndent"/> + N whole indent units" — the shape
    /// <see cref="ApplyDerivedTableStackedLayout"/> emits, and the only shape a prefix swap can move
    /// without inventing an indentation for the lines it lands on.
    ///
    /// The test is not a formality. ScriptDom's own column alignment shares the opener's indent as a
    /// prefix often enough to look movable while not being movable: some of its lines are whole units past
    /// the base and some are one or two columns past it, so swapping the prefix moves half a subquery and
    /// leaves the other half. Requiring the whole block to be unit-indented is what keeps this pass from
    /// touching a body it did not lay out.
    ///
    /// The look-ahead runs over lines with the enclosing blocks' moves already applied — a nested derived
    /// table's base indent is whatever the outer block's move left it at, not what is still sitting in the
    /// input array.
    /// </summary>
    private static bool BlockIsUnitIndented(string[] lines, int openerIndex, int openerDepth, string baseIndent,
                                            FormatterOptions options, List<BlockMove> outerMoves)
    {
        int depth = Math.Max(0, openerDepth + ParenDelta(lines[openerIndex]));

        for (int i = openerIndex + 1; i < lines.Length && depth > openerDepth; i++)
        {
            depth = Math.Max(0, depth + ParenDelta(lines[i]));

            string line = lines[i];
            foreach (var move in outerMoves)
                line = move.Apply(line);

            if (line.Trim().Length == 0)
                continue;
            if (!line.StartsWith(baseIndent, StringComparison.Ordinal))
                return false;

            string rest = line.Substring(baseIndent.Length);
            int ws = 0;
            while (ws < rest.Length && (rest[ws] == ' ' || rest[ws] == '\t')) ws++;
            if (!IsWholeIndentUnits(rest.Substring(0, ws), options))
                return false;
        }

        return true;
    }

    /// <summary>True for whitespace made of whole indent units in the configured style.</summary>
    private static bool IsWholeIndentUnits(string whitespace, FormatterOptions options)
    {
        if (whitespace.Length == 0)
            return true;

        if (options.IndentStyle == IndentStyleOption.Tabs)
            return whitespace.IndexOf(' ') < 0;

        return whitespace.IndexOf('\t') < 0 && whitespace.Length % Math.Max(1, options.IndentSize) == 0;
    }

    /// <summary>
    /// A pending re-indent of a bracketed block, applied as a **prefix swap** rather than a column shift.
    /// The stacked-derived-table pass emits every line of such a block as "opener indent + N indent units",
    /// so swapping the opener's indent for its new one moves the block exactly, tabs included — where a
    /// column shift would have to land on fractions of a tab. A block laid out any other way (ScriptDom's
    /// own column alignment, say) simply fails the prefix test and is left exactly where it was.
    /// </summary>
    private sealed class BlockMove
    {
        public BlockMove(int depth, string oldIndent, string newIndent)
        {
            Depth = depth;
            _oldIndent = oldIndent;
            _newIndent = newIndent;
        }

        public int Depth { get; }
        private readonly string _oldIndent;
        private readonly string _newIndent;

        public string Apply(string line) =>
            line.StartsWith(_oldIndent, StringComparison.Ordinal) && line.Trim().Length > 0
                ? _newIndent + line.Substring(_oldIndent.Length)
                : line;
    }

    /// <summary>
    /// Reflows INSERT column and VALUES lists to respect both InsertColumnsPerLine
    /// and MaxLineWidth, wrapping at whichever limit is hit first.
    /// ScriptDom keeps INSERT columns inline: "INSERT INTO table (col1, col2, col3, ...)"
    /// This splits them across lines when they exceed limits.
    /// Also handles VALUES (...) and one-per-line formats from ScriptDom.
    /// </summary>
    private static string ApplyInsertWrapping(string sql, FormatterOptions options)
    {
        if (options.InsertColumnsPerLine <= 0 && options.InsertValuesPerLine <= 0)
            return sql;

        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string indent = options.IndentStyle == IndentStyleOption.Tabs
            ? new string('\t', 1)
            : new string(' ', options.IndentSize);

        for (int i = 0; i < lines.Length; i++)
        {
            string stripped = lines[i].TrimStart();
            string lineIndent = lines[i].Substring(0, lines[i].Length - stripped.Length);

            // Case 1: INSERT INTO table (col1, col2, ...) all on one line
            var insertMatch = Regex.Match(stripped,
                @"^(INSERT\s+INTO\s+\S+\s*)\((.+)\)\s*$", RegexOptions.IgnoreCase);

            if (insertMatch.Success && options.InsertColumnsPerLine > 0)
            {
                string prefix = insertMatch.Groups[1].Value;
                string columnList = insertMatch.Groups[2].Value;
                var columns = SplitCsvItems(columnList);

                if (columns.Count > options.InsertColumnsPerLine ||
                    lines[i].TrimEnd().Length > options.MaxLineWidth)
                {
                    if (options.InsertOpenParenthesisOnSameLine)
                    {
                        // INSERT INTO table (
                        //     col1
                        //     , col2
                        // )
                        result.Add(lineIndent + prefix.TrimEnd() + " (");
                        ReflowItems(result, columns, lineIndent + indent,
                            options.InsertColumnsPerLine, options);
                        result.Add(lineIndent + ")");
                    }
                    else if (options.InsertParenthesesOnSameLine)
                    {
                        // INSERT INTO table (col1, col2,
                        //     col3, col4)
                        var reflowed = new List<string>();
                        ReflowItems(reflowed, columns, lineIndent + indent,
                            options.InsertColumnsPerLine, options);
                        if (reflowed.Count > 0)
                        {
                            result.Add(lineIndent + prefix.TrimEnd() + " (" + reflowed[0].TrimStart());
                            for (int r = 1; r < reflowed.Count - 1; r++)
                                result.Add(reflowed[r]);
                            if (reflowed.Count > 1)
                                result.Add(reflowed[reflowed.Count - 1] + ")");
                            else
                                result[result.Count - 1] += ")";
                        }
                    }
                    else
                    {
                        result.Add(lineIndent + prefix.TrimEnd());
                        result.Add(lineIndent + "(");
                        ReflowItems(result, columns, lineIndent + indent,
                            options.InsertColumnsPerLine, options);
                        result.Add(lineIndent + ")");
                    }
                    continue;
                }
            }

            // Case 2: VALUES (val1, val2, ...) all on one line
            var valuesMatch = Regex.Match(stripped,
                @"^(VALUES\s*)\((.+)\)(;?)\s*$", RegexOptions.IgnoreCase);

            if (valuesMatch.Success && options.InsertValuesPerLine > 0)
            {
                string prefix = valuesMatch.Groups[1].Value;
                string valueList = valuesMatch.Groups[2].Value;
                string trailing = valuesMatch.Groups[3].Value;
                var values = SplitCsvItems(valueList);

                if (values.Count > options.InsertValuesPerLine ||
                    lines[i].TrimEnd().Length > options.MaxLineWidth)
                {
                    if (options.InsertOpenParenthesisOnSameLine)
                    {
                        result.Add(lineIndent + prefix.TrimEnd() + " (");
                        ReflowItems(result, values, lineIndent + indent,
                            options.InsertValuesPerLine, options);
                        result.Add(lineIndent + ")" + trailing);
                    }
                    else if (options.InsertParenthesesOnSameLine)
                    {
                        var reflowed = new List<string>();
                        ReflowItems(reflowed, values, lineIndent + indent,
                            options.InsertValuesPerLine, options);
                        if (reflowed.Count > 0)
                        {
                            result.Add(lineIndent + prefix.TrimEnd() + " (" + reflowed[0].TrimStart());
                            for (int r = 1; r < reflowed.Count - 1; r++)
                                result.Add(reflowed[r]);
                            if (reflowed.Count > 1)
                                result.Add(reflowed[reflowed.Count - 1] + ")" + trailing);
                            else
                                result[result.Count - 1] += ")" + trailing;
                        }
                    }
                    else
                    {
                        result.Add(lineIndent + prefix.TrimEnd());
                        result.Add(lineIndent + "(");
                        ReflowItems(result, values, lineIndent + indent,
                            options.InsertValuesPerLine, options);
                        result.Add(lineIndent + ")" + trailing);
                    }
                    continue;
                }
            }

            // Case 3: Standalone "(" after INSERT INTO or VALUES — one-per-line format
            if (stripped == "(" && i > 0)
            {
                string prevStripped = lines[i - 1].TrimStart().TrimEnd();
                bool isInsert = Regex.IsMatch(prevStripped, @"\bINSERT\s+INTO\b", RegexOptions.IgnoreCase) ||
                                Regex.IsMatch(prevStripped, @"\bINTO\s+\S+\s*$", RegexOptions.IgnoreCase);
                bool isValues = prevStripped.Equals("VALUES", StringComparison.OrdinalIgnoreCase) ||
                                Regex.IsMatch(prevStripped, @"^VALUES\s*$", RegexOptions.IgnoreCase);

                int itemsPerLine = isInsert ? options.InsertColumnsPerLine
                                 : isValues ? options.InsertValuesPerLine
                                 : 0;

                if (itemsPerLine > 0)
                {
                    string parenLine = lines[i]; // Save "(" line
                    i++;
                    var items = new List<string>();
                    string itemIndent = null;
                    string closingLine = null;

                    while (i < lines.Length)
                    {
                        string itemStripped = lines[i].TrimStart();
                        if (itemStripped.StartsWith(")"))
                        {
                            closingLine = lines[i];
                            break;
                        }

                        if (itemIndent == null)
                            itemIndent = lines[i].Substring(0, lines[i].Length - itemStripped.Length);

                        string item = itemStripped.TrimEnd();
                        if (item.EndsWith(","))
                            item = item.Substring(0, item.Length - 1).TrimEnd();

                        items.Add(item);
                        i++;
                    }

                    string effectiveIndent = itemIndent ?? lineIndent + indent;

                    if (options.InsertOpenParenthesisOnSameLine && result.Count > 0)
                    {
                        // Merge ( onto the previous line only; the items keep their own lines and the
                        // closing ) keeps its own.
                        result[result.Count - 1] = result[result.Count - 1].TrimEnd() + " (";
                        if (items.Count > 0)
                            ReflowItems(result, items, effectiveIndent, itemsPerLine, options);
                        result.Add(closingLine ?? (lineIndent + ")"));
                    }
                    else if (options.InsertParenthesesOnSameLine && items.Count > 0)
                    {
                        // Merge ( onto previous line, reflow, merge ) onto last line
                        var reflowed = new List<string>();
                        ReflowItems(reflowed, items, effectiveIndent, itemsPerLine, options);

                        if (result.Count > 0 && reflowed.Count > 0)
                        {
                            result[result.Count - 1] += " (" + reflowed[0].TrimStart();
                            for (int r = 1; r < reflowed.Count; r++)
                                result.Add(reflowed[r]);
                        }

                        string closingSuffix = closingLine != null
                            ? closingLine.TrimStart() : ")";
                        if (result.Count > 0)
                            result[result.Count - 1] += closingSuffix;
                    }
                    else
                    {
                        result.Add(parenLine);
                        if (items.Count > 0)
                            ReflowItems(result, items, effectiveIndent, itemsPerLine, options);
                        if (closingLine != null)
                            result.Add(closingLine);
                    }

                    continue;
                }
            }

            result.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// Splits a comma-separated list, respecting parenthesised expressions like function calls.
    /// </summary>
    private static List<string> SplitCsvItems(string csv)
    {
        var items = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                items.Add(csv.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        string last = csv.Substring(start).Trim();
        if (last.Length > 0)
            items.Add(last);

        return items;
    }

    /// <summary>
    /// Groups items onto lines respecting both items-per-line and max-line-width constraints, and
    /// places the separators according to <see cref="FormatterOptions.CommaPosition"/>.
    ///
    /// The reflowed lists (INSERT targets, VALUES, procedure parameters) are the ones ScriptDom emits
    /// on a single line, so the leading-comma pass — which works line to line — never sees them and
    /// they were the one place a leading-comma profile still produced trailing commas. Grouping is
    /// decided before rendering precisely so that comma position cannot change *where* the list wraps:
    /// the same list has to break at the same items under either setting.
    /// </summary>
    private static void ReflowItems(List<string> result, List<string> items, string indent, int itemsPerLine, FormatterOptions options)
    {
        if (items.Count == 0) return;
        indent = indent ?? "";
        int maxLineWidth = options.MaxLineWidth;

        var groups = new List<List<string>>();
        var current = new List<string>();
        int lineLength = indent.Length;

        for (int j = 0; j < items.Count; j++)
        {
            string item = items[j];
            // width of the item plus the separator before it (" ") and after it (",")
            int cost = (current.Count > 0 ? 1 : 0) + item.Length + (j == items.Count - 1 ? 0 : 1);

            bool exceedsWidth = maxLineWidth > 0 && current.Count > 0 && lineLength + cost > maxLineWidth;
            bool exceedsCount = current.Count >= itemsPerLine;

            if (exceedsWidth || exceedsCount)
            {
                groups.Add(current);
                current = new List<string>();
                lineLength = indent.Length;
                cost = item.Length + (j == items.Count - 1 ? 0 : 1);
            }

            lineLength += cost;
            current.Add(item);
        }

        if (current.Count > 0)
            groups.Add(current);

        bool leading = options.CommaPosition == CommaPositionOption.LeadingComma;

        for (int g = 0; g < groups.Count; g++)
        {
            string content = string.Join(", ", groups[g]);
            if (leading)
                result.Add(g == 0 ? indent + content : PrependLeadingComma(indent, content, options));
            else
                result.Add(indent + content + (g == groups.Count - 1 ? "" : ","));
        }
    }

    /// <summary>
    /// Wraps CREATE/ALTER PROCEDURE/FUNCTION parameter lists.
    /// ScriptDom puts all parameters on one line; this wraps them at MaxLineWidth.
    /// </summary>
    private static string ApplyProcedureParameterWrapping(string sql, FormatterOptions options)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string indent = options.IndentStyle == IndentStyleOption.Tabs
            ? new string('\t', 1)
            : new string(' ', options.IndentSize);

        for (int i = 0; i < lines.Length; i++)
        {
            string stripped = lines[i].TrimStart();
            string lineIndent = lines[i].Substring(0, lines[i].Length - stripped.Length);

            // Detect: line starts with CREATE/ALTER PROCEDURE/FUNCTION/PROC
            // and the NEXT line contains the parameter list (starts with @)
            if (Regex.IsMatch(stripped, @"^(CREATE|ALTER)\s+(PROCEDURE|PROC|FUNCTION)\b", RegexOptions.IgnoreCase) &&
                i + 1 < lines.Length)
            {
                string nextStripped = lines[i + 1].TrimStart();

                // Parameters are on the next line, starting with @
                if (nextStripped.StartsWith("@"))
                {
                    string paramLine = lines[i + 1].TrimEnd();

                    // Only wrap if the line exceeds max width
                    if (options.MaxLineWidth > 0 && paramLine.Length > options.MaxLineWidth)
                    {
                        result.Add(lines[i]); // Add CREATE/ALTER PROCEDURE line
                        i++;

                        // Split parameters by ", @" keeping the @ with the parameter
                        var parameters = SplitProcedureParameters(nextStripped);
                        string paramIndent = lineIndent + indent;
                        ReflowItems(result, parameters, paramIndent, int.MaxValue, options);
                        continue;
                    }
                }

                // Parameters might be on the same line as CREATE/ALTER PROCEDURE
                var paramMatch = Regex.Match(stripped,
                    @"^((CREATE|ALTER)\s+(PROCEDURE|PROC|FUNCTION)\s+\S+\s+)(@.+)$",
                    RegexOptions.IgnoreCase);

                if (paramMatch.Success)
                {
                    string prefix = paramMatch.Groups[1].Value;
                    string paramsPart = paramMatch.Groups[4].Value;

                    if (options.MaxLineWidth > 0 && lines[i].TrimEnd().Length > options.MaxLineWidth)
                    {
                        result.Add(lineIndent + prefix.TrimEnd());
                        var parameters = SplitProcedureParameters(paramsPart);
                        string paramIndent = lineIndent + indent;
                        ReflowItems(result, parameters, paramIndent, int.MaxValue, options);
                        continue;
                    }
                }
            }

            result.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// Splits procedure parameters like "@a INT, @b VARCHAR(50)=NULL" into individual parameters.
    /// </summary>
    private static List<string> SplitProcedureParameters(string paramText)
    {
        var items = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < paramText.Length; i++)
        {
            char c = paramText[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                items.Add(paramText.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        string last = paramText.Substring(start).Trim();
        if (last.Length > 0)
            items.Add(last);

        return items;
    }

    /// <summary>
    /// Inserts a blank line before SELECT, INSERT, UPDATE, DELETE statements
    /// that aren't already preceded by a blank line or at the start of the text.
    /// </summary>
    private static string ApplyBlankLineBeforeStatements(string sql)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string stripped = lines[i].TrimStart();

            if (i > 0 && Regex.IsMatch(stripped, @"^(SELECT|INSERT|UPDATE|DELETE|ALTER|CREATE|DROP)\b", RegexOptions.IgnoreCase))
            {
                // Walk backwards past blank lines to find the last meaningful line
                string prevStripped = null;
                for (int p = i - 1; p >= 0; p--)
                {
                    if (!string.IsNullOrWhiteSpace(lines[p]))
                    {
                        prevStripped = lines[p].TrimStart().TrimEnd();
                        break;
                    }
                }

                string prevLine = lines[i - 1].TrimEnd();
                bool prevIsBlank = string.IsNullOrWhiteSpace(prevLine);
                bool prevIsOpenParen = prevStripped != null && prevStripped.EndsWith("(");
                bool isIndented = lines[i].Length - stripped.Length > 0;

                // Don't add blank line if this keyword is a continuation of the same statement:
                // - SELECT after INSERT INTO ... (...) — prev line is ")" from column list
                // - SELECT/UPDATE/DELETE after a CTE WITH block — prev line ends with ")"  or "AS"
                // - Any DML keyword that's indented (subquery, EXISTS, etc.)
                bool isContinuation = false;

                if (prevStripped != null)
                {
                    // SELECT after INSERT INTO table / INSERT INTO table (...)
                    if (Regex.IsMatch(stripped, @"^SELECT\b", RegexOptions.IgnoreCase))
                    {
                        isContinuation = Regex.IsMatch(prevStripped, @"\bINTO\b", RegexOptions.IgnoreCase) ||
                                         (prevStripped == ")" && IsInsertContext(lines, i));
                    }

                    // UPDATE inside a MERGE or similar compound statement
                    if (Regex.IsMatch(stripped, @"^(UPDATE|INSERT|DELETE)\b", RegexOptions.IgnoreCase))
                    {
                        isContinuation = isContinuation ||
                                         Regex.IsMatch(prevStripped, @"^THEN\s*$", RegexOptions.IgnoreCase);
                    }
                }

                if (!prevIsBlank && !prevIsOpenParen && !isIndented && !isContinuation)
                {
                    result.Add("");
                }
            }

            result.Add(lines[i]);
        }

        return string.Join(Environment.NewLine, result);
    }

    /// <summary>
    /// Scans backwards from a ")" line to determine if it belongs to an INSERT column list.
    /// </summary>
    private static bool IsInsertContext(string[] lines, int selectLineIndex)
    {
        // Walk backwards from the line before SELECT, looking for the matching "("
        // then checking if the line before that is INSERT INTO
        int depth = 0;
        for (int p = selectLineIndex - 1; p >= 0; p--)
        {
            string s = lines[p].TrimStart().TrimEnd();
            if (s.EndsWith(")") || s == ")") depth++;
            if (s.StartsWith("(") || s == "(") depth--;

            if (depth <= 0)
            {
                // Check if the line before this "(" or the line itself contains INSERT INTO
                if (Regex.IsMatch(s, @"\bINSERT\s+INTO\b", RegexOptions.IgnoreCase))
                    return true;
                if (p > 0)
                {
                    string above = lines[p - 1].TrimStart().TrimEnd();
                    if (Regex.IsMatch(above, @"\bINSERT\s+INTO\b", RegexOptions.IgnoreCase))
                        return true;
                }
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Reflows CTE (WITH ... AS ( ... )) blocks into stacked layout:
    ///     WITH cteName AS (
    ///         &lt;body&gt;
    ///     )
    ///
    ///     , cteNext AS (
    ///         &lt;body&gt;
    ///     )
    /// The opening "(" ends the WITH/"," line; the body is de-indented one level; the closing
    /// ")" drops to the left margin; a blank line separates each CTE and precedes the main
    /// query. Layout only — CTE names are not renamed. Best-effort: returns the input unchanged
    /// if anything unexpected is encountered.
    /// </summary>
    private static string ApplyCteStackedLayout(string sql, FormatterOptions options)
    {
        if (string.IsNullOrEmpty(sql) || !Regex.IsMatch(sql, @"(?im)^\s*WITH\b"))
            return sql;

        try
        {
            string indentUnit = options.IndentStyle == IndentStyleOption.Tabs
                ? "\t"
                : new string(' ', Math.Max(1, options.IndentSize));

            var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new List<string>();
            int n = lines.Length;
            int i = 0;

            // Emit everything up to (and including nothing of) the first CTE's WITH line.
            while (i < n && !Regex.IsMatch(lines[i].TrimStart(), @"^WITH\s+\S", RegexOptions.IgnoreCase))
            {
                result.Add(lines[i]);
                i++;
            }
            if (i >= n)
                return string.Join(Environment.NewLine, result);

            // A CTE header is assembled by joining lines — the name, the AS and the "(" arrive on as many
            // lines as ScriptDom felt like using — and any "--" comment among them would put every line
            // joined after it inside the comment. ScriptDom really does produce that: the comment trailing
            // the last line of one CTE's body lands after the ")," that separates it from the next
            // ("FROM dbo.A AS a), -- note"), so the remainder becomes this CTE's name and the emitted header
            // reads ", -- note Second AS (" — the whole of the next CTE commented out, which is still a
            // parse error two CTEs later rather than anything that points here. So comments are lifted out
            // of the header as it is built and re-emitted as their own lines in front of it.
            var pendingComments = new List<string>();

            string TakeComment(string text)
            {
                int c = FindLineCommentStart(text);
                if (c < 0)
                    return text;

                pendingComments.Add(text.Substring(c).TrimEnd());
                return text.Substring(0, c).TrimEnd();
            }

            // The WITH keyword introduces the first CTE name.
            string currentName = TakeComment(Regex.Replace(lines[i].TrimStart(), @"^WITH\s+", "", RegexOptions.IgnoreCase).Trim());
            i++;
            bool first = true;

            while (true)
            {
                // Accumulate the header until it contains "AS (" (name and AS may be on separate lines).
                string headerText = currentName;
                int guard = 0;
                while (i < n && !Regex.IsMatch(headerText, @"\bAS\s*\(", RegexOptions.IgnoreCase) && guard++ < 200)
                {
                    headerText += " " + TakeComment(lines[i].TrimStart());
                    i++;
                }

                var hm = Regex.Match(headerText, @"^(?<name>.*?)\bAS\s*\((?<inline>.*)$", RegexOptions.IgnoreCase);
                if (!hm.Success)
                    return sql; // unexpected shape — leave the whole script untouched

                result.AddRange(pendingComments);
                pendingComments.Clear();
                result.Add((first ? "WITH " : ", ") + hm.Groups["name"].Value.Trim() + " AS (");

                int depth = 1;
                string inline = hm.Groups["inline"].Value.Trim();
                if (inline.Length > 0)
                {
                    result.Add(indentUnit + inline);
                    depth += ParenDelta(inline);
                }

                // Consume the body until the paren that closes this CTE.
                string after = "";
                while (i < n)
                {
                    string bl = lines[i];
                    int delta = ParenDelta(bl);
                    if (depth + delta > 0)
                    {
                        result.Add(bl);
                        depth += delta;
                        i++;
                        continue;
                    }

                    int closeIdx = IndexClosingParen(bl, depth);
                    if (closeIdx < 0)
                        return sql; // couldn't locate the close — bail safely

                    string before = bl.Substring(0, closeIdx).TrimEnd();
                    after = TakeComment(bl.Substring(closeIdx + 1).Trim()).Trim();
                    if (before.Length > 0)
                        result.Add(before);
                    result.Add(")");
                    i++;
                    break;
                }

                // A comma after the ")" (either trailing here or leading the next line) means another CTE follows.
                bool moreFollow = after.StartsWith(",");
                if (moreFollow)
                    after = after.Substring(1).Trim();
                else
                {
                    int peek = i;
                    while (peek < n && string.IsNullOrWhiteSpace(lines[peek])) peek++;
                    if (peek < n && lines[peek].TrimStart().StartsWith(","))
                    {
                        moreFollow = true;
                        i = peek;
                        lines[i] = lines[i].TrimStart().Substring(1); // drop the leading comma
                    }
                }

                if (moreFollow)
                {
                    result.Add(""); // blank line between CTEs
                    first = false;
                    if (after.Length > 0)
                    {
                        currentName = after;
                    }
                    else
                    {
                        while (i < n && string.IsNullOrWhiteSpace(lines[i])) i++;
                        if (i >= n)
                            return sql;
                        currentName = TakeComment(lines[i].TrimStart());
                        i++;
                    }
                    continue;
                }

                // No more CTEs — blank line before the main query, then the remainder verbatim.
                result.AddRange(pendingComments);
                pendingComments.Clear();
                result.Add("");
                if (after.Length > 0)
                    result.Add(after);
                while (i < n)
                {
                    result.Add(lines[i]);
                    i++;
                }
                break;
            }

            return string.Join(Environment.NewLine, result);
        }
        catch
        {
            return sql;
        }
    }

    /// <summary>A table-reference keyword occupying its whole line — ScriptDom puts the JOIN keyword on
    /// one line and the reference on the next.</summary>
    private static readonly Regex TableRefKeywordOnly = new Regex(
        @"^(FROM|INNER\s+JOIN|LEFT\s+(OUTER\s+)?JOIN|RIGHT\s+(OUTER\s+)?JOIN|FULL\s+(OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN|CROSS\s+APPLY|OUTER\s+APPLY)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The same keywords ending the text that precedes a "(". Not anchored to the start of the
    /// line: ScriptDom used to keep an APPLY on the FROM line ("FROM A AS a CROSS APPLY (SELECT …"), so
    /// requiring the keyword at column zero would have missed every CROSS/OUTER APPLY. 180.107.0 gives the
    /// APPLY its own line, so the anchored form would now match too — this stays un-anchored because it
    /// costs nothing and the generator has already moved this once.</summary>
    private static readonly Regex TableRefKeywordBeforeParen = new Regex(
        @"(^|\s)(FROM|INNER\s+JOIN|LEFT\s+(OUTER\s+)?JOIN|RIGHT\s+(OUTER\s+)?JOIN|FULL\s+(OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN|CROSS\s+APPLY|OUTER\s+APPLY)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reflows a derived table — a subquery in FROM, JOIN or APPLY — into the stacked shape
    /// <see cref="ApplyCteStackedLayout"/> gives a CTE:
    ///
    ///     LEFT JOIN (
    ///         SELECT x
    ///         , y
    ///         FROM B
    ///     ) AS bb ON bb.x = a.Id
    ///
    /// ScriptDom instead aligns the body under whatever column the "(" happened to land in — a different
    /// column for every join in the statement, and one that stops meaning anything the moment another pass
    /// re-indents the line the "(" is on, which is exactly what `CollapseJoinLayout` and
    /// `ApplyAlignFromAndJoins` do to it.
    ///
    /// Two things about where and how it runs are load-bearing:
    /// - **Early, before the SELECT-column and comma passes**, so the body is normalized by them like any
    ///   other query. That is what makes a stacked derived table read like a stacked CTE instead of merely
    ///   being moved sideways, and it is the same reason the CTE pass runs where it does.
    /// - **Body lines are emitted as "opener indent + N indent units"**, never as a column count. That
    ///   shape is what lets `ApplyAlignFromAndJoins` move the finished block by swapping the base prefix.
    ///   A rigid column shift would have to land on fractions of a tab.
    ///
    /// Best-effort throughout: anything unexpected (an unbalanced block, a shape not recognised) leaves
    /// the input alone, because a half-reflowed FROM clause is worse than an ugly one.
    /// </summary>
    private static string ApplyDerivedTableStackedLayout(string sql, FormatterOptions options) =>
        StackDerivedTables(sql, options, 0);

    private static string StackDerivedTables(string sql, FormatterOptions options, int nesting)
    {
        if (string.IsNullOrEmpty(sql) || nesting > 8)
            return sql;

        try
        {
            var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var result = new List<string>();
            string unit = options.IndentStyle == IndentStyleOption.Tabs ? "\t" : new string(' ', Math.Max(1, options.IndentSize));
            int tabSize = Math.Max(1, options.IndentSize);

            for (int i = 0; i < lines.Length; i++)
            {
                string stripped = lines[i].TrimStart();
                string indent = lines[i].Substring(0, lines[i].Length - stripped.Length);
                int indentWidth = LeadingWidth(lines[i], tabSize);

                // Shape 1: "LEFT JOIN (SELECT …".  Shape 2: "LEFT JOIN" alone, "(SELECT …" on the next line.
                string keyword = null, inlineTail = null;
                int parenColumn = 0, openerLine = i;

                int parenIdx = FindDerivedTableParen(stripped);
                if (parenIdx >= 0)
                {
                    keyword = stripped.Substring(0, parenIdx).TrimEnd();
                    inlineTail = stripped.Substring(parenIdx + 1);
                    parenColumn = indentWidth + parenIdx;
                }
                else if (TableRefKeywordOnly.IsMatch(stripped) && i + 1 < lines.Length)
                {
                    string next = lines[i + 1].TrimStart();
                    if (next.StartsWith("(") && OpensSubquery(next, 0))
                    {
                        keyword = stripped;
                        inlineTail = next.Substring(1);
                        parenColumn = LeadingWidth(lines[i + 1], tabSize);
                        openerLine = i + 1;
                    }
                }

                if (keyword == null)
                {
                    result.Add(lines[i]);
                    continue;
                }

                // The body runs to the ")" that closes the subquery. The text before it on that line is the
                // last body line; everything from the ")" on is the reference's tail ( ") AS bb" ).
                var body = new List<string>();
                if (inlineTail.Trim().Length > 0)
                    body.Add(new string(' ', parenColumn + 1) + inlineTail.Trim());

                int depth = 1 + ParenDelta(inlineTail);
                int j = openerLine + 1;
                string tail = null;

                while (j < lines.Length)
                {
                    int closeIdx = IndexClosingParen(lines[j], depth);
                    if (closeIdx < 0)
                    {
                        body.Add(lines[j]);
                        depth += ParenDelta(lines[j]);
                        j++;
                        continue;
                    }

                    string before = lines[j].Substring(0, closeIdx).TrimEnd();
                    if (before.Trim().Length > 0)
                        body.Add(before);
                    tail = lines[j].Substring(closeIdx).Trim();
                    break;
                }

                if (tail == null || body.Count == 0)
                {
                    result.Add(lines[i]);           // unbalanced or empty — leave the shape alone
                    continue;
                }

                // A derived table can hold another one; reflow the body before re-indenting it, so the
                // nested block is already in "base + N units" form when the relative offsets are measured.
                var reflowed = StackDerivedTables(string.Join(Environment.NewLine, body), options, nesting + 1)
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

                int baseWidth = int.MaxValue;
                foreach (var b in reflowed)
                {
                    if (b.Trim().Length > 0)
                        baseWidth = Math.Min(baseWidth, LeadingWidth(b, tabSize));
                }
                if (baseWidth == int.MaxValue) baseWidth = 0;

                result.Add(indent + keyword + " (");
                foreach (var b in reflowed)
                {
                    if (b.Trim().Length == 0) { result.Add(""); continue; }

                    // Offsets inside the body come from ScriptDom's column alignment, so they are rounded to
                    // whole indent units rather than carried across as columns.
                    int rel = (int)Math.Round((LeadingWidth(b, tabSize) - baseWidth) / (double)tabSize, MidpointRounding.AwayFromZero);
                    result.Add(indent + Repeat(unit, 1 + Math.Max(0, rel)) + b.TrimStart());
                }
                result.Add(indent + tail);

                i = j;
            }

            return string.Join(Environment.NewLine, result);
        }
        catch
        {
            return sql;
        }
    }

    /// <summary>
    /// Index of the "(" on this line that opens a derived table, or -1. The paren has to be preceded by a
    /// table-reference keyword and followed by a SELECT that does not close on the line — which is what
    /// keeps this off "WHERE x IN (SELECT …", "WHERE EXISTS (SELECT …" and a scalar subquery in a SELECT
    /// list, none of which are table references and none of which may be reflowed as one. Parens inside
    /// string literals, bracketed identifiers and comments are skipped rather than tested.
    /// </summary>
    private static int FindDerivedTableParen(string s)
    {
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, n); continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-') break;          // rest of the line is a comment
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipBlockComment(s, i, n); continue; }

            if (c == '(' && OpensSubquery(s, i) && TableRefKeywordBeforeParen.IsMatch(s.Substring(0, i)))
                return i;

            i++;
        }
        return -1;
    }

    /// <summary>True when the "(" at <paramref name="parenIdx"/> opens a SELECT that does not close on
    /// this line — i.e. a multi-line derived table rather than a short inline one worth leaving alone.</summary>
    private static bool OpensSubquery(string s, int parenIdx)
    {
        if (parenIdx < 0 || parenIdx >= s.Length || s[parenIdx] != '(')
            return false;

        int k = parenIdx + 1;
        while (k < s.Length && char.IsWhiteSpace(s[k])) k++;
        if (!MatchWordCI(s, k, "SELECT"))
            return false;

        return ParenDelta(s.Substring(parenIdx)) > 0;
    }

    /// <summary>Display width of a line's leading whitespace, counting a tab as one indent unit.</summary>
    private static int LeadingWidth(string line, int tabSize)
    {
        int w = 0;
        foreach (char c in line)
        {
            if (c == '\t') w += tabSize;
            else if (c == ' ') w++;
            else break;
        }
        return w;
    }

    private static string Repeat(string unit, int count)
    {
        if (count <= 0) return "";
        var sb = new StringBuilder(unit.Length * count);
        for (int k = 0; k < count; k++) sb.Append(unit);
        return sb.ToString();
    }

    /// <summary>
    /// Puts the SELECT keyword alone on its line and stacks every column on its own line,
    /// indented one level under SELECT. FROM/WHERE/etc. (and lines inside nested parentheses)
    /// are left where they are. Runs before the comma pass so leading/trailing comma layout
    /// is applied afterward to the normalized indentation.
    /// </summary>
    private static string ApplyStackSelectColumns(string sql, FormatterOptions options)
    {
        var lines = sql.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var result = new List<string>();
        string indentUnit = options.IndentStyle == IndentStyleOption.Tabs
            ? "\t"
            : new string(' ', Math.Max(1, options.IndentSize));

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string stripped = line.TrimStart();

            var m = Regex.Match(stripped,
                @"^(?<kw>SELECT)(?<mods>(\s+DISTINCT)?(\s+TOP\s*\(?\s*\d+\s*\)?(\s+PERCENT)?(\s+WITH\s+TIES)?)?)(?<rest>\s.*|$)",
                RegexOptions.IgnoreCase);

            if (!m.Success)
            {
                result.Add(line);
                continue;
            }

            string selIndent = line.Substring(0, line.Length - stripped.Length);
            string colIndent = selIndent + indentUnit;
            result.Add(selIndent + ("SELECT" + m.Groups["mods"].Value).TrimEnd());

            int depth = 0;
            string rest = m.Groups["rest"].Value.Trim();
            if (rest.Length > 0)
            {
                result.Add(colIndent + rest);
                depth += ParenDelta(rest);
            }

            int j = i + 1;
            while (j < lines.Length)
            {
                string s = lines[j].TrimStart();

                if (depth == 0 &&
                    (string.IsNullOrWhiteSpace(s) || s.StartsWith(";") ||
                     Regex.IsMatch(s, @"^(FROM|WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|EXCEPT|INTERSECT|INTO|FOR\b|OPTION\b|\))", RegexOptions.IgnoreCase)))
                    break;

                // Only re-indent column lines at the SELECT's own paren depth; leave the
                // interior of a multi-line column expression / subquery untouched.
                result.Add(depth == 0 ? colIndent + s.TrimEnd() : lines[j]);
                depth += ParenDelta(lines[j]);
                j++;
            }

            i = j - 1;
        }

        return string.Join(Environment.NewLine, result);
    }

    // --- CASE layout ---------------------------------------------------------------------------

    /// <summary>
    /// Reflows every CASE expression so each WHEN (and the ELSE) starts its own line, with END back at the
    /// column the CASE keyword sits in.
    ///
    /// ScriptDom has no CASE layout of its own: it emits the whole expression on one line and breaks it in
    /// exactly one place — where a WHEN's condition is a multi-part boolean and MultilineWherePredicatesList
    /// is on — indenting that continuation to the column the condition started in. On a CASE with several
    /// WHENs those columns compound, so the tail of the expression walks hundreds of characters off to the
    /// right. Both are the same problem, and reflowing fixes both: the pass **flattens the region first**
    /// (every whitespace run outside a literal becomes one space) and then re-emits it, so the staircase is
    /// discarded rather than added to.
    ///
    /// Two things this must not do, in the order they were found:
    ///
    /// - **Column offsets are spelled with spaces; the base indent is copied verbatim.** A nested CASE sits
    ///   mid-line ("ISNULL(CASE"), so its body has to align to a column no tab can address. The prefix is
    ///   therefore the line's own leading whitespace (tabs and all) followed by one space per remaining
    ///   character up to the CASE — the only invented part is the part that cannot be an indent.
    /// - **A "--" comment inside the region is lifted onto a line of its own** rather than left where it
    ///   was. Flattening a region that contains one would pull the rest of the CASE into the comment, which
    ///   is the failure this file has shipped twice (see RestoreCommentLinePlacement, ApplyCteStackedLayout).
    ///   Emitting them as their own lines keeps them near where they were written and — unlike bailing out
    ///   on the first comment — leaves the option doing what it says on a script that has any.
    /// </summary>
    private static string ApplyCaseWhenLayout(string sql, FormatterOptions options)
    {
        if (string.IsNullOrEmpty(sql) || options.CaseWhenLayout == CaseWhenLayoutOption.Unchanged)
            return sql;

        try
        {
            string s = sql;
            int i = 0;

            for (int guard = 0; guard < 10000; guard++)
            {
                int caseIdx = FindKeyword(s, i, "CASE");
                if (caseIdx < 0)
                    break;

                // Resume inside this CASE whatever happens next: a nested one is found on the following
                // iteration, by which point the text around it is already in its final shape.
                i = caseIdx + 4;

                int endIdx = FindMatchingCaseEnd(s, caseIdx + 4);
                if (endIdx < 0)
                    continue;

                string reflowed = RenderCase(s, caseIdx, endIdx, options);
                if (reflowed == null)
                    continue;

                s = s.Substring(0, caseIdx) + reflowed + s.Substring(endIdx);
            }

            return s;
        }
        catch
        {
            return sql;   // a layout option is never worth a formatter that gives back nothing
        }
    }

    /// <summary>
    /// Renders [caseIdx, endIdx) — "CASE" through to just before its "END" — in the requested layout,
    /// ending with the newline and indent that puts the caller's untouched "END" at the CASE's column.
    /// Null when the region holds no WHEN at its own level, which is not a CASE this pass can read.
    /// </summary>
    private static string RenderCase(string s, int caseIdx, int endIdx, FormatterOptions options)
    {
        var clauses = SplitCaseClauses(s, caseIdx + 4, endIdx);
        if (clauses.Count == 0 || !string.Equals(clauses[0].Keyword, "WHEN", StringComparison.OrdinalIgnoreCase))
            return null;

        string caseIndent = IndentToColumn(s, caseIdx);
        string unit = options.IndentStyle == IndentStyleOption.Tabs ? "\t" : new string(' ', Math.Max(1, options.IndentSize));
        string nl = Environment.NewLine;

        // Text between CASE and the first WHEN — the input of a simple CASE ("CASE @x WHEN 1 …").
        var input = FlattenCaseSegment(s, caseIdx + 4, clauses[0].Start);

        var sb = new StringBuilder();
        sb.Append("CASE");
        if (input.Text.Length > 0)
            sb.Append(' ').Append(input.Text);

        string bodyIndent;
        int firstClause = 0;

        if (options.CaseWhenLayout == CaseWhenLayoutOption.WhenAligned)
        {
            var head = FlattenCaseSegment(s, clauses[0].Start, clauses[0].End);
            bodyIndent = caseIndent + new string(' ', sb.Length + 1);   // under the first WHEN
            sb.Append(' ').Append(head.Text);
            foreach (var c in input.Comments) sb.Append(nl).Append(bodyIndent).Append(c);
            foreach (var c in head.Comments) sb.Append(nl).Append(bodyIndent).Append(c);
            firstClause = 1;
        }
        else
        {
            bodyIndent = caseIndent + unit;
            foreach (var c in input.Comments) sb.Append(nl).Append(bodyIndent).Append(c);
        }

        for (int k = firstClause; k < clauses.Count; k++)
        {
            var flat = FlattenCaseSegment(s, clauses[k].Start, clauses[k].End);
            sb.Append(nl).Append(bodyIndent).Append(flat.Text);
            foreach (var c in flat.Comments)
                sb.Append(nl).Append(bodyIndent).Append(c);
        }

        sb.Append(nl).Append(caseIndent);
        return sb.ToString();
    }

    /// <summary>
    /// Whitespace that reaches the column <paramref name="index"/> sits in: the line's own leading
    /// whitespace copied verbatim (so a tab-indented script stays tab-indented), then one space per
    /// character of the rest. Anything past the indent is code — "ISNULL(" before a nested CASE — and
    /// spaces are the only thing that can line up with it.
    /// </summary>
    private static string IndentToColumn(string s, int index)
    {
        int lineStart = index <= 0 ? 0 : s.LastIndexOf('\n', index - 1) + 1;

        var sb = new StringBuilder(index - lineStart);
        bool inIndent = true;
        for (int k = lineStart; k < index; k++)
        {
            char c = s[k];
            if (inIndent && (c == ' ' || c == '\t')) { sb.Append(c); continue; }
            inIndent = false;
            sb.Append(' ');
        }
        return sb.ToString();
    }

    private struct CaseClause
    {
        public string Keyword;   // "WHEN" or "ELSE"
        public int Start;        // index of the keyword
        public int End;          // index just past the clause
    }

    /// <summary>
    /// The WHEN/ELSE clauses of one CASE, split at the keywords that are at the CASE's own level — depth 0
    /// for parentheses (a WHEN inside a subquery belongs to that subquery) and outside any nested CASE
    /// (whose WHENs are reflowed on their own turn, against their own indent).
    /// </summary>
    private static List<CaseClause> SplitCaseClauses(string s, int start, int end)
    {
        var clauses = new List<CaseClause>();
        int depth = 0, caseDepth = 0, i = start;

        while (i < end)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, end); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, end); continue; }
            if (c == '-' && i + 1 < end && s[i + 1] == '-') { i = SkipLineComment(s, i, end); continue; }
            if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipBlockComment(s, i, end); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }

            if (IsWordStart(s, i))
            {
                if (MatchWordCI(s, i, "CASE")) { caseDepth++; i += 4; continue; }
                if (MatchWordCI(s, i, "END")) { if (caseDepth > 0) caseDepth--; i += 3; continue; }

                if (depth == 0 && caseDepth == 0)
                {
                    string kw = MatchWordCI(s, i, "WHEN") ? "WHEN" : MatchWordCI(s, i, "ELSE") ? "ELSE" : null;
                    if (kw != null)
                    {
                        if (clauses.Count > 0)
                        {
                            var previous = clauses[clauses.Count - 1];
                            previous.End = i;
                            clauses[clauses.Count - 1] = previous;
                        }
                        clauses.Add(new CaseClause { Keyword = kw, Start = i, End = end });
                        i += kw.Length;
                        continue;
                    }
                }
            }

            i++;
        }

        return clauses;
    }

    private struct FlatSegment
    {
        public string Text;
        public List<string> Comments;
    }

    /// <summary>
    /// One clause as a single line: whitespace runs collapsed to one space, and every "--" comment removed
    /// and handed back separately for the caller to re-emit on its own line. String literals, bracketed
    /// identifiers and block comments are copied through byte-for-byte — a literal may legally span lines,
    /// and collapsing the spaces inside a quoted identifier would rename it.
    /// </summary>
    private static FlatSegment FlattenCaseSegment(string s, int start, int end)
    {
        var sb = new StringBuilder(Math.Max(0, end - start));
        var comments = new List<string>();
        int i = start;
        bool pendingSpace = false;

        while (i < end)
        {
            char c = s[i];

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) pendingSpace = true;
                i++;
                continue;
            }

            if (c == '-' && i + 1 < end && s[i + 1] == '-')
            {
                int stop = SkipLineComment(s, i, end);
                string comment = s.Substring(i, stop - i).TrimEnd();
                if (comment.Length > 0) comments.Add(comment);
                if (sb.Length > 0) pendingSpace = true;
                i = stop;
                continue;
            }

            int next =
                c == '\'' ? SkipSingleQuote(s, i, end) :
                c == '[' ? SkipBracketToken(s, i, end) :
                (c == '/' && i + 1 < end && s[i + 1] == '*') ? SkipBlockComment(s, i, end) :
                i + 1;

            if (pendingSpace && sb.Length > 0) sb.Append(' ');
            pendingSpace = false;
            sb.Append(s, i, Math.Min(next, end) - i);
            i = next;
        }

        return new FlatSegment { Text = sb.ToString().Trim(), Comments = comments };
    }

    /// <summary>Index of the END that closes the CASE whose body starts at <paramref name="from"/>, or -1.
    /// Nested CASEs are counted; nothing else inside an expression can open an END.</summary>
    private static int FindMatchingCaseEnd(string s, int from)
    {
        int depth = 0, i = from, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '\'') { i = SkipSingleQuote(s, i, n); continue; }
            if (c == '[') { i = SkipBracketToken(s, i, n); continue; }
            if (c == '-' && i + 1 < n && s[i + 1] == '-') { i = SkipLineComment(s, i, n); continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipBlockComment(s, i, n); continue; }

            if (IsWordStart(s, i))
            {
                if (MatchWordCI(s, i, "CASE")) { depth++; i += 4; continue; }
                if (MatchWordCI(s, i, "END"))
                {
                    if (depth == 0) return i;
                    depth--;
                    i += 3;
                    continue;
                }
            }

            i++;
        }
        return -1;
    }

    /// <summary>Net parenthesis count for a line, ignoring '(' / ')' inside string literals,
    /// bracketed identifiers, and after a line comment.</summary>
    private static int ParenDelta(string line)
    {
        int delta = 0;
        bool inStr = false, inBracket = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                if (c == '\'')
                {
                    if (i + 1 < line.Length && line[i + 1] == '\'') { i++; continue; }
                    inStr = false;
                }
                continue;
            }
            if (inBracket)
            {
                if (c == ']') inBracket = false;
                continue;
            }
            if (c == '\'') { inStr = true; continue; }
            if (c == '[') { inBracket = true; continue; }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') break;
            if (c == '(') delta++;
            else if (c == ')') delta--;
        }
        return delta;
    }

    /// <summary>Index of the ')' that brings the running paren depth (from startDepth) to zero;
    /// -1 if it doesn't happen on this line. String/comment/bracket aware.</summary>
    private static int IndexClosingParen(string line, int startDepth)
    {
        int depth = startDepth;
        bool inStr = false, inBracket = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                if (c == '\'')
                {
                    if (i + 1 < line.Length && line[i + 1] == '\'') { i++; continue; }
                    inStr = false;
                }
                continue;
            }
            if (inBracket)
            {
                if (c == ']') inBracket = false;
                continue;
            }
            if (c == '\'') { inStr = true; continue; }
            if (c == '[') { inBracket = true; continue; }
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') break;
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
