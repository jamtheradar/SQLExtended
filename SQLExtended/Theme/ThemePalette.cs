using System;
using System.Collections.Generic;

namespace SQLExtended.Theme;

/// <summary>
/// Every colour the extension's own WPF chrome paints with, as a named role with a dark and a light value.
///
/// <para>XAML never names a colour directly: it asks for a role — <c>{DynamicResource SqlxSurface}</c> — and
/// <see cref="ThemeManager"/> installs the variant that matches the shell. The dark column is the palette the
/// extension shipped with before light support existed, so a dark-theme user sees no change — bar a handful of
/// near-duplicate greys (#DDDDDD and #BBBBBB text, #444444 and #666666 outlines, and the like) folded into their
/// neighbour, which were drift between hand-copied styles rather than choices.
/// The light column is designed against the SSMS light theme (white surfaces, #1E1E1E text, the same #007ACC
/// accent) with every text role clearing 4.5:1 on the surface it sits on — <c>ThemePaletteTests</c> holds that.</para>
///
/// <para>Roles are split by what they paint, not by the hex they happened to share in the dark theme: #333337
/// was both a control fill and a hairline, which is fine on black and wrong on white, so it is two roles here.</para>
///
/// <para>These are the fallbacks. The chrome roles — surfaces, text, borders, selection, buttons — are read from
/// whichever theme is picked in SSMS (see <c>ThemeManager.ShellRoles</c>), so Blue or a custom theme is matched
/// exactly; <see cref="Resolve"/> merges the two. Status and syntax roles always come from here.</para>
///
/// <para>Pure data with no WPF dependency, so the test project links it.</para>
/// </summary>
internal static class ThemePalette
{
    public readonly struct Entry(string key, string dark, string light)
    {
        public string Key { get; } = key;
        public string Dark { get; } = dark;
        public string Light { get; } = light;
    }

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // ---- Surfaces ----
        new("SqlxSurface",           "#1E1E1E", "#FFFFFF"),   // window and grid background
        new("SqlxSurfaceSunken",     "#1A1A1A", "#F0F0F0"),   // progress-bar wells
        new("SqlxSurfaceAlt",        "#252526", "#F5F5F5"),   // panels, cards, inactive tabs
        new("SqlxSurfaceRaised",     "#2D2D30", "#EEEEF2"),   // headers, popups, totals rows
        new("SqlxRowAlt",            "#242425", "#F7F7F9"),   // alternate grid row
        new("SqlxGridRowAlt",        "#22262B", "#F4F7FA"),   // alternate row in the monitoring grids
        new("SqlxRowHover",          "#2A2D2E", "#E8F2FB"),
        new("SqlxControl",           "#333337", "#F7F7F9"),   // button and text box fill
        new("SqlxControlHover",      "#3F3F46", "#DDE7F2"),   // hover fill; also vertical separators
        new("SqlxControlPressed",    "#007ACC", "#C4DDF3"),
        new("SqlxSelection",         "#094771", "#CCE4F7"),
        new("SqlxSelectionText",     "#FFFFFF", "#1E1E1E"),
        new("SqlxAccent",            "#007ACC", "#007ACC"),
        new("SqlxOnAccent",          "#FFFFFF", "#FFFFFF"),   // text on an accent fill
        new("SqlxOverlay",           "#33000000", "#14000000"), // translucent chip behind a badge
        new("SqlxProgress",          "#0E639C", "#0E70C0"),

        // ---- Lines ----
        new("SqlxDivider",           "#2A2A2C", "#E6E6EA"),
        new("SqlxBorderSubtle",      "#333337", "#E3E4E8"),
        new("SqlxBorder",            "#3F3F46", "#CCCEDB"),
        new("SqlxBorderStrong",      "#555555", "#A9ADB8"),   // control outlines

        // ---- Text ----
        new("SqlxText",              "#D4D4D4", "#1E1E1E"),   // content: grid cells, editors
        new("SqlxTextChrome",        "#CCCCCC", "#262626"),   // labels, buttons, headers
        new("SqlxTextStrong",        "#FFFFFF", "#000000"),   // titles, selected tab
        new("SqlxTextSubtle",        "#999999", "#595959"),
        new("SqlxTextMuted",         "#808080", "#6B6B6B"),
        new("SqlxTextDisabled",      "#666666", "#A0A0A0"),
        new("SqlxLineNumber",        "#555555", "#8C8C8C"),
        new("SqlxHeading",           "#569CD6", "#1F5FAD"),

        // ---- Syntax-like accents, matching the editor's own colouring in each theme ----
        new("SqlxSynIdentifier",     "#9CDCFE", "#001080"),
        new("SqlxSynFunction",       "#DCDCAA", "#795E26"),
        new("SqlxSynComment",        "#6A9955", "#008000"),
        new("SqlxSynPurple",         "#C586C0", "#AF00DB"),
        new("SqlxSynNumber",         "#B5CEA8", "#07704A"),

        // ---- Status: good / degraded / bad, and their row and banner tints ----
        new("SqlxGood",              "#4EC9B0", "#0B7A6B"),
        new("SqlxWarn",              "#D7BA7D", "#8A6100"),
        new("SqlxBad",               "#F48771", "#C42B1C"),
        new("SqlxError",             "#F14C4C", "#C42B1C"),
        new("SqlxHighlight",         "#F0C674", "#8A6100"),
        new("SqlxWarnRow",           "#38301F", "#FFF4D6"),
        new("SqlxWarnSurface",       "#3A2F24", "#FDF3E1"),
        new("SqlxWarnSurfaceStrong", "#4A3B28", "#F9E6BF"),
        new("SqlxWarnBorder",        "#6B5836", "#D9B870"),
        new("SqlxBanner",            "#3A2D00", "#FFF4CE"),
        new("SqlxBannerBorder",      "#7A6000", "#E0C050"),
        new("SqlxBadRow",            "#3A2426", "#FDE7E9"),

        // ---- Schema Compare's diff (painted under syntax colours, so tuned to keep the changed words readable) ----
        new("SqlxDiffChangedLine",   "#3A3418", "#FFF4C2"),
        new("SqlxDiffChangedWords",  "#6E5C12", "#FFD25E"),
        new("SqlxDiffLeftLine",      "#4A2427", "#FFDCDC"),
        new("SqlxDiffRightLine",     "#1E3D27", "#D8F3DA"),
        new("SqlxDiffFiller",        "#262628", "#ECEDEF"),
        new("SqlxDiffCurrent",       "#3794FF", "#2563EB"),   // outline round the current difference
        new("SqlxDiffMarkChanged",   "#C9A227", "#E0B12E"),   // overview bar marks
        new("SqlxDiffMarkLeft",      "#E06C75", "#F08A8A"),
        new("SqlxDiffMarkRight",     "#4EC9B0", "#5DBB6A"),
        new("SqlxDiffViewport",      "#A0A0A8", "#7A8290"),
    ];

    private static readonly Dictionary<string, Entry> ByKey = Index();

    private static Dictionary<string, Entry> Index()
    {
        var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in Entries)
            map[e.Key] = e;

        return map;
    }

    public static bool Contains(string key) => ByKey.ContainsKey(key);

    /// <summary>The hex for <paramref name="key"/> in the given variant. Throws on an unknown key — a typo should fail loudly in tests.</summary>
    public static string Hex(string key, bool dark) => dark ? ByKey[key].Dark : ByKey[key].Light;

    /// <summary>Parses #RRGGBB or #AARRGGBB into ARGB bytes.</summary>
    public static (byte A, byte R, byte G, byte B) Parse(string hex)
    {
        string h = hex.TrimStart('#');
        uint v = Convert.ToUInt32(h, 16);

        return h.Length == 8 ? ((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v) : ((byte)0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>The same perceived-brightness cut the comment-scheme applier uses, so both features agree on what "dark" is.</summary>
    public static bool IsDarkBackground(byte r, byte g, byte b) => (0.299 * r) + (0.587 * g) + (0.114 * b) < 128;

    /// <summary>
    /// Shades the shell has no colour for, made by moving the themed surface a fixed way towards the themed text.
    /// The fractions reproduce the dark column (#1E1E1E → #252525 for the alt surface, #333333 for a hairline), so
    /// in any theme they sit the same visual distance from the background as they do in the stock dark one.
    /// </summary>
    private static readonly (string Role, double Towards)[] Derived =
    [
        ("SqlxSurfaceAlt",   0.04),
        ("SqlxRowAlt",       0.025),
        ("SqlxGridRowAlt",   0.03),
        ("SqlxRowHover",     0.08),
        ("SqlxBorderSubtle", 0.115),
    ];

    /// <summary>
    /// Foreground / background pairs the XAML puts together, with the contrast each must reach. If the shell's pair
    /// misses, the background (and the foreground, if it too came from the shell) falls back to the built-in variant.
    /// A theme is free to pair colours we never anticipated; this is what stops that from becoming unreadable text.
    ///
    /// <para>Most of these pairs are ours — tool-window text laid on a button fill, say — so they are held to AA.
    /// Selection is the theme's own designed pair (both from TreeViewColors) and is trusted down to 2.5:1: VS Light
    /// and Blue deliberately ship white on #3399FF at 2.9:1, and overruling that would stop us matching the theme.</para>
    /// </summary>
    private static readonly (string Fore, string Back, double Min)[] Guards =
    [
        ("SqlxText",          "SqlxSurface",        4.5),
        ("SqlxTextChrome",    "SqlxSurface",        4.5),
        ("SqlxTextChrome",    "SqlxSurfaceRaised",  4.5),
        ("SqlxTextChrome",    "SqlxControl",        4.5),
        ("SqlxTextChrome",    "SqlxControlHover",   3.0),
        ("SqlxTextStrong",    "SqlxControlPressed", 3.0),
        ("SqlxSelectionText", "SqlxSelection",      2.5),
        ("SqlxOnAccent",      "SqlxAccent",         3.0),
    ];

    /// <summary>
    /// The final colour for every role: the shell's colour where it has one (<paramref name="shell"/>, role → hex),
    /// derived shades from the shell's surface and text, and the built-in <paramref name="dark"/> or light variant
    /// for everything else — status and syntax colours, which no VS theme defines.
    /// </summary>
    public static Dictionary<string, string> Resolve(bool dark, IReadOnlyDictionary<string, string> shell)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in Entries)
            result[e.Key] = dark ? e.Dark : e.Light;

        var fromShell = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in shell)
        {
            if (!ByKey.ContainsKey(pair.Key) || string.IsNullOrEmpty(pair.Value)) continue;
            result[pair.Key] = pair.Value;
            fromShell.Add(pair.Key);
        }

        // Derived shades only when both ends are the theme's; otherwise the built-in value is the better match.
        if (fromShell.Contains("SqlxSurface") && fromShell.Contains("SqlxText"))
        {
            foreach (var (role, towards) in Derived)
            {
                if (fromShell.Contains(role)) continue;
                result[role] = Mix(result["SqlxSurface"], result["SqlxText"], towards);
                fromShell.Add(role);
            }
        }

        foreach (var (fore, back, min) in Guards)
        {
            if (Contrast(result[fore], result[back]) >= min) continue;

            result[back] = Hex(back, dark);
            if (fromShell.Contains(fore) && Contrast(result[fore], result[back]) < min)
                result[fore] = Hex(fore, dark);
        }

        return result;
    }

    /// <summary>Moves <paramref name="from"/> the fraction <paramref name="towards"/> of the way to <paramref name="to"/>. Opaque result.</summary>
    public static string Mix(string from, string to, double towards)
    {
        var (_, r1, g1, b1) = Parse(from);
        var (_, r2, g2, b2) = Parse(to);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + ((b - a) * towards));

        return $"#{Lerp(r1, r2):X2}{Lerp(g1, g2):X2}{Lerp(b1, b2):X2}";
    }

    /// <summary>WCAG contrast ratio between two colours, 1 to 21. Alpha is ignored.</summary>
    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var (_, r, g, b) = Parse(hex);
        static double Channel(byte c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }
}
