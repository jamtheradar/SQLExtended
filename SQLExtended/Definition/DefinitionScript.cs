using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLExtended.Definition;

/// <summary>Which statement Open Definition scripts a module (procedure, view, function, trigger) as.</summary>
public enum DefinitionScriptMode
{
    /// <summary>The definition exactly as stored — <c>CREATE</c>, which fails against the object it came from.</summary>
    Create,
    Alter,
    /// <summary>Runs whether or not the object exists. SQL Server 2016 SP1 and later.</summary>
    CreateOrAlter
}

/// <summary>
/// Turns a View Schema script into what Open Definition puts in a query window: a <c>USE</c> for the object's database,
/// then the script with a module's <c>CREATE</c> rewritten to the chosen mode.
///
/// <para>Free of SqlClient and the shell so the test project can link it. What is worth pinning is which <c>CREATE</c>
/// gets rewritten: a module's stored text very often opens with a comment block that itself says
/// "create procedure …" (a change log, a template header), and rewriting that instead of the statement leaves a script
/// that still fails on F5 and a comment nobody asked to have edited.</para>
/// </summary>
public static class DefinitionScript
{
    private static readonly Regex ModuleCreate = new(
        @"\GCREATE(?<orAlter>\s+OR\s+ALTER)?(?=\s+(PROC|PROCEDURE|VIEW|FUNCTION|TRIGGER)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>USE [database]</c> and <c>GO</c> ahead of <paramref name="script"/>, with the module statement in
    /// <paramref name="mode"/> (a null mode leaves it as it is). A null or empty database leaves the <c>USE</c> out.
    /// </summary>
    public static string Build(string database, string script, DefinitionScriptMode? mode)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(database))
        {
            sb.Append("USE ").Append(QuoteName(database)).Append("\r\nGO\r\n\r\n");
        }
        sb.Append(mode is { } m ? ApplyMode(script, m) : script ?? "");
        return sb.ToString();
    }

    /// <summary>
    /// Rewrites the first <c>CREATE [OR ALTER] PROC|PROCEDURE|VIEW|FUNCTION|TRIGGER</c> outside comments and strings to
    /// <paramref name="mode"/>. Anything else — a table script, an encrypted module's explanatory comment — comes back
    /// unchanged, as does a statement already in the requested form.
    /// </summary>
    public static string ApplyMode(string script, DefinitionScriptMode mode)
    {
        if (string.IsNullOrEmpty(script)) return script ?? "";

        int at = FirstCreateKeyword(script);
        if (at < 0) return script;

        Match match = ModuleCreate.Match(script, at);
        if (!match.Success) return script;

        string replacement = mode switch
        {
            DefinitionScriptMode.Alter => "ALTER",
            DefinitionScriptMode.CreateOrAlter => "CREATE OR ALTER",
            _ => "CREATE"
        };
        return script.Substring(0, match.Index) + replacement + script.Substring(match.Index + match.Length);
    }

    /// <summary>Index of the first <c>CREATE</c> word outside comments, strings and quoted identifiers, or -1.</summary>
    private static int FirstCreateKeyword(string sql)
    {
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && next == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '\'' || c == '"' || c == '[')
            {
                i = SkipQuoted(sql, i, c == '[' ? ']' : c);
            }
            else if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
            {
                int start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '@' || sql[i] == '#' || sql[i] == '$'))
                    i++;
                if (i - start == 6 && string.Compare(sql, start, "CREATE", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
                    return start;
            }
            else
            {
                i++;
            }
        }
        return -1;
    }

    /// <summary>T-SQL block comments nest; the end is the <c>*/</c> that closes the outermost one.</summary>
    private static int SkipBlockComment(string sql, int i)
    {
        int depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
            else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0) return i;
            }
            else i++;
        }
        return sql.Length;
    }

    /// <summary>Past a quoted run, where a doubled closing character is an escaped one.</summary>
    private static int SkipQuoted(string sql, int i, char close)
    {
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";
}
