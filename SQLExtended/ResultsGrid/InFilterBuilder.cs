using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SQLExtended.ResultsGrid;

/// <summary>
/// Builds the <c>IN (...)</c> list "Copy as IN Filter" puts on the clipboard (issue #5): one value per line, indented four
/// spaces, NULLs left out, text quoted with its quotes doubled.
///
/// <para>Free of the grid assembly so the test project can link it. The decision worth pinning is quoting. It goes by the
/// column's SQL type when SSMS gives one: a <c>varchar</c> of digits is text, and pasting <c>007</c> unquoted turns it into
/// the number 7, which matches a different row or none — a filter that runs and quietly answers the wrong question. Only
/// without a type does it fall back to the values themselves, and then a leading zero keeps a value quoted.</para>
/// </summary>
public static class InFilterBuilder
{
    private static readonly HashSet<string> UnquotedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "bit", "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real",
        // Rendered as 0x… hex, which is already a binary literal.
        "binary", "varbinary", "image", "timestamp", "rowversion"
    };

    private static readonly HashSet<string> UnicodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nchar", "nvarchar", "ntext", "sysname", "xml"
    };

    /// <summary>
    /// The <c>IN</c> list for <paramref name="values"/> in the order given, or null when every value is NULL.
    /// <paramref name="sqlType"/> is the column's type as SSMS formats it (<c>nvarchar(50)</c>, <c>decimal(18,2)</c>), or
    /// null when unknown.
    /// </summary>
    public static string Build(IEnumerable<string> values, string sqlType)
    {
        var present = new List<string>();
        foreach (string value in values)
        {
            if (value != null) present.Add(value);
        }
        if (present.Count == 0) return null;

        string baseType = BaseType(sqlType);
        bool quote = baseType != null ? !UnquotedTypes.Contains(baseType) : !AllLookNumeric(present);
        bool unicode = baseType != null && UnicodeTypes.Contains(baseType);

        var sb = new StringBuilder("IN (\r\n");
        for (int i = 0; i < present.Count; i++)
        {
            sb.Append("    ").Append(quote ? Quote(present[i], unicode) : present[i].Trim());
            sb.Append(i < present.Count - 1 ? ",\r\n" : "\r\n");
        }
        return sb.Append(')').ToString();
    }

    /// <summary><c>decimal(18,2)</c> → <c>decimal</c>; null for an empty type.</summary>
    private static string BaseType(string sqlType)
    {
        if (string.IsNullOrWhiteSpace(sqlType)) return null;
        string type = sqlType.Trim();
        int paren = type.IndexOf('(');
        return (paren > 0 ? type.Substring(0, paren) : type).Trim();
    }

    /// <summary>
    /// A literal for a text value. <c>N'…'</c> only when the column is Unicode and the value needs it, so the common case
    /// pastes as plain <c>'…'</c> and a name in another script is not turned into question marks.
    /// </summary>
    private static string Quote(string value, bool unicode)
    {
        bool needsN = false;
        if (unicode)
        {
            foreach (char c in value)
            {
                if (c > 127) { needsN = true; break; }
            }
        }
        return (needsN ? "N'" : "'") + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// Without a type: numeric only if every value parses as one and none has a leading zero (<c>007</c> is an identifier
    /// someone typed, not a number). Invariant culture, no thousands separators — the grid renders numbers unseparated.
    /// </summary>
    private static bool AllLookNumeric(List<string> values)
    {
        foreach (string raw in values)
        {
            string value = raw.Trim();
            if (value.Length == 0) return false;
            if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _))
                return false;
            string digits = value.TrimStart('-', '+');
            if (digits.Length > 1 && digits[0] == '0' && digits[1] != '.') return false;
        }
        return true;
    }
}
