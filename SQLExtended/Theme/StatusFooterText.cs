using System.Text.RegularExpressions;

namespace SQLExtended.Theme;

/// <summary>
/// Pure: whether a status line's text says work is still running. Every window writes its in-progress lines with a
/// trailing ellipsis ("Exporting to C:\……", "Searching...", a monitor's "Reading waits…  (2 of 5)") and its settled
/// lines without one, so the text alone tells <see cref="StatusFooter"/> when to show the busy look. Linked into the tests.
/// </summary>
internal static class StatusFooterText
{
    // An ellipsis (… or ...) at the end, optionally followed by a step count: "(2 of 5)" or "(2/5)".
    private static readonly Regex Running = new(@"(\u2026|\.\.\.)\s*(\(\s*[\d,]+\s*(of|/)\s*[\d,]+\s*\))?\s*$", RegexOptions.CultureInvariant);

    public static bool LooksBusy(string text) => !string.IsNullOrWhiteSpace(text) && Running.IsMatch(text);
}
