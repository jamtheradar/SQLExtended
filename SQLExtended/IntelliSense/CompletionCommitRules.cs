using System;

namespace SQLExtended.IntelliSense;

/// <summary>
/// When the completion list may take a keystroke as "accept this item" (issue #11). Free of the editor assemblies so
/// the test project links it, the same split <see cref="SqlIdentifierQuoting"/> exists for.
/// <para>Space is the dangerous commit character: it is also how every word ends, so a list that has an item
/// hard-selected at the moment a word is finished replaces that word. <c>DELETE FROM</c> + Space became
/// <c>DELETE DATEFROMPARTS</c> — the list after DELETE held no plain FROM, the substring match was the best it had,
/// and it was hard-selected. Earlier fixes added the missing keyword to one context at a time (ON after a JOIN, FROM
/// after SELECT); these two rules close the class instead.</para>
/// </summary>
internal static class CompletionCommitRules
{
    /// <summary>
    /// True when <paramref name="typed"/> starts the item's name: its display text, the part after the last dot, or
    /// any of the space-separated forms in its filter text (the bracketed and quoted forms live there). Only such an
    /// item is hard-selected; a substring or camel-hump match is still listed but soft-selected, so finishing a word
    /// that merely occurs inside a name does not accept that name.
    /// </summary>
    public static bool IsWordStartMatch(string typed, string displayText, string filterText)
    {
        if (string.IsNullOrEmpty(typed))
            return false;

        string display = displayText ?? string.Empty;
        if (display.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            return true;

        int dot = display.LastIndexOf('.');
        if (dot >= 0 && display.Substring(dot + 1).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string form in (filterText ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (form.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether Space may commit <paramref name="displayText"/> over <paramref name="typed"/>. Tab and Enter are not
    /// asked: they are deliberate, Space is not.
    /// </summary>
    public static bool SpaceMayCommit(string typed, string displayText, string filterText)
    {
        typed ??= string.Empty;
        string display = displayText ?? string.Empty;

        // Nothing typed: the list opened by itself after a keyword. Space there is the user moving on.
        if (typed.Trim().Length == 0)
            return false;

        // Multi-word items ("CREATE TABLE", "INNER JOIN") wait until the user has typed past the first word.
        if (display.IndexOf(' ') >= 0 && typed.IndexOf(' ') < 0)
            return false;

        // Finishing a word that only occurs inside the item's name (FROM in DATEFROMPARTS).
        if (!IsWordStartMatch(typed, display, filterText))
            return false;

        // A complete reserved word is what the user meant: IS must not become ISNULL, IN must not become INT,
        // AS must not become ASCII. Only the keyword itself may be committed over it.
        if (SqlIdentifierQuoting.IsReservedKeyword(typed) && !string.Equals(BareName(display), typed, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string BareName(string display)
    {
        int dot = display.LastIndexOf('.');
        return dot >= 0 ? display.Substring(dot + 1) : display;
    }
}
