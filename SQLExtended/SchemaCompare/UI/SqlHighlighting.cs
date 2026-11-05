using System.Collections.Generic;
using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// The extension's T-SQL highlighting (<c>SQLExtended/Search/TsqlDarkHighlighting.xshd</c>, linked as a resource): as is
/// for the dark theme, and with its foregrounds swapped for the light editor's for the light one, the same swap
/// <c>SQLExtended/Theme/TsqlHighlighting.cs</c> makes. Keep the two maps in step.
/// </summary>
internal static class SqlHighlighting
{
    private const string ResourceName = "SQLExtended.Search.TsqlDarkHighlighting.xshd"; // The extension's name for it; the shell gives its link the same LogicalName.

    private static readonly Dictionary<string, string> LightForegrounds = new()
    {
        ["#6A9955"] = "#008000", // comment
        ["#CE9178"] = "#A31515", // string
        ["#569CD6"] = "#0000FF", // keyword
        ["#DCDCAA"] = "#795E26", // function
        ["#4EC9B0"] = "#267F99", // data type
        ["#B5CEA8"] = "#098658", // number
        ["#D4D4D4"] = "#1E1E1E", // operator
        ["#9CDCFE"] = "#001080", // variable
        ["#C586C0"] = "#AF00DB", // system view
        ["#D7BA7D"] = "#8A6100", // temp table
    };

    private static IHighlightingDefinition _dark, _light;
    private static bool _darkLoaded, _lightLoaded;

    public static IHighlightingDefinition For(bool dark)
    {
        if (dark)
        {
            if (!_darkLoaded) { _dark = Load(dark: true); _darkLoaded = true; }
            return _dark;
        }

        if (!_lightLoaded) { _light = Load(dark: false); _lightLoaded = true; }
        return _light;
    }

    private static IHighlightingDefinition Load(bool dark)
    {
        try
        {
            using var stream = typeof(SqlHighlighting).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var text = new StreamReader(stream);
            string xshd = text.ReadToEnd();
            if (!dark)
            {
                foreach (var pair in LightForegrounds) xshd = xshd.Replace($"foreground=\"{pair.Key}\"", $"foreground=\"{pair.Value}\"");
            }

            using var reader = new XmlTextReader(new StringReader(xshd));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            return null; // Uncoloured text still diffs.
        }
    }
}
