using System;
using System.Text;

namespace SQLExtended.Cache;

/// <summary>
/// The one definition of "the search term occurs in this text" for SQL Search — object and column names, the
/// SQLite definition pass, Agent job steps and the preview highlighter all go through it, so a result is never
/// listed by one rule and highlighted (or not) by another.
///
/// <para><b>Match whole word</b> uses T-SQL identifier characters as word characters — letters, digits,
/// <c>_</c>, <c>@</c>, <c>#</c> and <c>$</c> — not Visual Studio's letters-digits-underscore. So <c>person</c>
/// finds <c>dbo.person</c>, <c>[person]</c> and <c>WHERE person = 1</c>, but not <c>person_id</c>,
/// <c>@person</c> or <c>#person</c>, each of which is a different name. As in Visual Studio, a boundary is
/// only required on a side where the term itself starts or ends with a word character: <c>[person]</c> or
/// <c>.person</c> needs nothing further, since the bracket or dot is already the edge.</para>
///
/// <para>Comparison is <see cref="StringComparison.OrdinalIgnoreCase"/>, as it always was here.</para>
/// </summary>
internal static class SearchTextMatcher
{
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

    public static bool Contains(string text, string term, bool wholeWord) => IndexOf(text, term, 0, wholeWord) >= 0;

    /// <summary>Index of the first match at or after <paramref name="startIndex"/>, or -1.</summary>
    public static int IndexOf(string text, string term, int startIndex, bool wholeWord)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return -1;

        bool checkStart = wholeWord && IsWordChar(term[0]);
        bool checkEnd = wholeWord && IsWordChar(term[term.Length - 1]);

        int index = startIndex;
        while (index <= text.Length - term.Length && (index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = index + term.Length;
            bool startOk = !checkStart || index == 0 || !IsWordChar(text[index - 1]);
            bool endOk = !checkEnd || end == text.Length || !IsWordChar(text[end]);
            if (startOk && endOk) return index;
            index++;
        }

        return -1;
    }

    /// <summary>
    /// <c>%term%</c> for SQLite's LIKE, with <c>%</c>, <c>_</c> and the escape character itself escaped by
    /// <see cref="SqliteLikeEscape"/>; the query must say <c>ESCAPE '\'</c>. Unescaped, a term containing
    /// <c>_</c> — which is most column names — matched any character there, and <c>%</c> matched anything.
    /// SQLite has no <c>[…]</c> class, so unlike the msdb pattern <c>[</c> is left alone.
    /// </summary>
    public static string BuildSqliteLikePattern(string term)
    {
        var sb = new StringBuilder(term.Length + 8);
        sb.Append('%');
        foreach (char c in term)
        {
            if (c == '%' || c == '_' || c == SqliteLikeEscape) sb.Append(SqliteLikeEscape);
            sb.Append(c);
        }
        sb.Append('%');
        return sb.ToString();
    }

    public const char SqliteLikeEscape = '\\';
}
