using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SQLExtended.SchemaCompare.Engine;

/// <summary>
/// What a compare ignores. Every flag removes one kind of noise from the normalised text of both sides. Nothing here
/// changes which objects are matched, only how their text reads, except <see cref="IgnoreSecurity"/> and
/// <see cref="ExcludeObjects"/>, which drop objects from the list.
/// </summary>
/// <remarks>Plain settable properties (no records/init): the engine is linked into the net48 test project.</remarks>
public sealed class CompareOptions
{
    private static readonly char[] ListSeparators = { ';', '\r', '\n' };

    // Text
    public bool IgnoreComments { get; set; } = true;
    public bool IgnoreWhitespace { get; set; } = true;
    public bool IgnoreCase { get; set; } = true;
    public bool IgnoreBrackets { get; set; } = true;
    public bool IgnoreSemicolons { get; set; } = true;
    public bool IgnoreSessionSettings { get; set; } = true;
    public bool IgnoreCreateOrAlter { get; set; } = true;

    // Tables and indexes
    public bool IgnoreDefaultIndexOptions { get; set; } = true;
    public bool IgnoreAllIndexOptions { get; set; }
    public bool IgnoreDefaultFilegroup { get; set; } = true;
    public bool IgnoreAllFilegroups { get; set; }
    public bool IgnoreCollation { get; set; } = true;
    public bool IgnoreSystemNames { get; set; } = true;
    public bool IgnoreConstraintTrust { get; set; } = true;
    public bool IgnoreRedundantParentheses { get; set; } = true;
    public bool IgnoreColumnOrder { get; set; }
    public bool IgnoreIndexes { get; set; }
    public bool IgnoreSchemaOwnerDbo { get; set; } = true;

    // Objects
    public bool IgnoreExtendedProperties { get; set; }
    public bool IgnoreSecurity { get; set; } = true;

    /// <summary>Wildcard patterns over "schema.name" (or "Type:schema.name"), separated by ';' or new lines.</summary>
    public string ExcludeObjects { get; set; } = "";

    /// <summary>
    /// Names that differ by environment and should read as the same, one pair per line: <c>left = right</c>. Typically a
    /// cross-database reference (<c>GENGER.Patient = GENGER-uat.Patient</c>) or a project's SQLCMD variable
    /// (<c>GENGER.Patient = $(PatientDb)</c>). Each right-hand name is rewritten to its left-hand one before comparing.
    /// </summary>
    public string EquivalentNames { get; set; } = "";

    public CompareOptions Clone()
    {
        var copy = (CompareOptions)MemberwiseClone();
        copy._patterns = null;
        copy._mappings = null;
        copy._reverseMappings = null;
        copy._lock = new object();
        return copy;
    }

    // Compare reads these caches from many threads at once, hence the lock.
    private object _lock = new();
    private string _mappingSource, _patternSource;
    private List<(Regex Find, string Replace)> _mappings;
    private List<Regex> _patterns;

    /// <summary>Applies <see cref="EquivalentNames"/> to the right side's text.</summary>
    public string MapRightNames(string text)
    {
        if (string.IsNullOrWhiteSpace(EquivalentNames) || string.IsNullOrEmpty(text)) return text;

        List<(Regex Find, string Replace)> mappings;
        lock (_lock)
        {
            if (_mappings == null || _mappingSource != EquivalentNames)
            {
                _mappingSource = EquivalentNames;
                _mappings = BuildMappings(EquivalentNames);
            }

            mappings = _mappings;
        }

        foreach (var (find, replace) in mappings) text = find.Replace(text, replace.Replace("$", "$$"));
        return text;
    }

    /// <summary>
    /// <see cref="EquivalentNames"/> the other way: each left-hand name rewritten to its right-hand one, for a change script
    /// that takes the left's definitions to the right, whose environment uses the right's names.
    /// </summary>
    public string MapLeftNames(string text)
    {
        if (string.IsNullOrWhiteSpace(EquivalentNames) || string.IsNullOrEmpty(text)) return text;

        List<(Regex Find, string Replace)> mappings;
        lock (_lock)
        {
            if (_reverseMappings == null || _reverseMappingSource != EquivalentNames)
            {
                _reverseMappingSource = EquivalentNames;
                _reverseMappings = BuildMappings(EquivalentNames, reverse: true);
            }

            mappings = _reverseMappings;
        }

        foreach (var (find, replace) in mappings) text = find.Replace(text, replace.Replace("$", "$$"));
        return text;
    }

    private string _reverseMappingSource;
    private List<(Regex Find, string Replace)> _reverseMappings;

    private static List<(Regex, string)> BuildMappings(string source, bool reverse = false)
    {
        var result = new List<(Regex, string)>();
        foreach (var line in source.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string left = Unwrap(line.Substring(0, eq)), right = Unwrap(line.Substring(eq + 1));
            if (left.Length == 0 || right.Length == 0) continue;
            if (reverse) (left, right) = (right, left);

            // The name bracketed, double-quoted or bare (bare only where it isn't part of a longer name).
            string r = Regex.Escape(right);
            result.Add((new Regex(@"\[" + r + @"\]", RegexOptions.IgnoreCase), "[" + left + "]"));
            result.Add((new Regex("\"" + r + "\"", RegexOptions.IgnoreCase), "\"" + left + "\""));
            result.Add((new Regex(@"(?<![\w\[\]$""])" + r + @"(?![\w\]""])", RegexOptions.IgnoreCase), left));
        }

        return result;
    }

    private static string Unwrap(string name)
    {
        name = name.Trim();
        if (name.Length >= 2 && ((name[0] == '[' && name[name.Length - 1] == ']') || (name[0] == '"' && name[name.Length - 1] == '"'))) name = name.Substring(1, name.Length - 2);
        return name;
    }

    /// <summary>True when the object should be left out of the compare entirely.</summary>
    public bool Excludes(SchemaObject obj)
    {
        if (IgnoreSecurity && obj.IsSecurity) return true;
        if (string.IsNullOrWhiteSpace(ExcludeObjects)) return false;

        foreach (var pattern in ExcludePatterns())
        {
            if (pattern.IsMatch(obj.DisplayName) || pattern.IsMatch(obj.Type + ":" + obj.DisplayName)) return true;
        }

        return false;
    }

    private List<Regex> ExcludePatterns()
    {
        lock (_lock)
        {
            if (_patterns == null || _patternSource != ExcludeObjects)
            {
                _patternSource = ExcludeObjects;
                _patterns = ExcludeObjects.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()).Where(p => p.Length > 0)
                    .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    .ToList();
            }

            return _patterns;
        }
    }
}
