namespace SQLExtended.Cache.Models;

internal sealed class SearchOptions
{
    /// <summary>Object type filter (e.g., "U", "V", "P"). Null means all types.</summary>
    public string TypeFilter { get; set; }

    /// <summary>Search in object names.</summary>
    public bool SearchObjectNames { get; set; } = true;

    /// <summary>Search in column names.</summary>
    public bool SearchColumnNames { get; set; } = true;

    /// <summary>Search in stored proc/function/view definitions.</summary>
    public bool SearchDefinitions { get; set; } = true;

    /// <summary>Match the term only as a whole word — see <see cref="SearchTextMatcher"/> for what a word is.</summary>
    public bool WholeWord { get; set; }

    /// <summary>
    /// Leave objects whose name matches out of the definition pass, because a name pass lists them. Set only
    /// when one ran: definitions-only (Find References) must keep them, or a vPerson using Person goes missing.
    /// </summary>
    public bool ExcludeNameMatchesFromDefinitions { get; set; }

    public int MaxResults { get; set; } = 200;
}
