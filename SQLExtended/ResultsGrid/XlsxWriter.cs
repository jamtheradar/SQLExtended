using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SQLExtended.ResultsGrid;

/// <summary>What <see cref="XlsxWriter.Write"/> had to do to the data to make a valid workbook.</summary>
public sealed class XlsxWriteResult
{
    public long Rows { get; set; }
    public int Columns { get; set; }

    /// <summary>Text cells cut to Excel's 32,767-character limit. Excel refuses ("repairs") a file with a longer cell.</summary>
    public int TruncatedCells { get; set; }
}

/// <summary>
/// A minimal Office Open XML (.xlsx) writer: one sheet, a bold frozen header row with an autofilter, and cells typed by the
/// column's SQL type — numbers as numbers, dates as dates, everything else as text.
///
/// <para>Typing goes by the SQL type, never by whether the text looks numeric, for the same reason <see cref="InFilterBuilder"/>
/// does: a <c>varchar</c> of <c>007</c> written as a number opens in Excel as 7. A column with no type is all text.</para>
///
/// <para>Wherever a typed cell would show Excel something other than what the grid showed, it is written as text instead,
/// because a rounded number in a spreadsheet looks exactly like a right one: an exact numeric with more than 15 significant
/// digits (Excel keeps 15), a date before 1900-03-01 (Excel's calendar starts in 1900 and wrongly has a 29 February), and a
/// time with precision below the millisecond (Excel keeps milliseconds). Text that does not parse is also kept as text.</para>
///
/// <para>Free of the grid assembly so the test project links it (<c>SQLExtended.Tests/ResultsGrid/XlsxWriterTests.cs</c>).</para>
/// </summary>
public static class XlsxWriter
{
    public const int MaxRows = 1048576;      // per sheet, header included
    public const int MaxColumns = 16384;
    public const int MaxCellText = 32767;

    internal enum CellKind { Text, Number, ExactNumber, Date, DateTime, DateTimeMs }

    // Style indexes into cellXfs below.
    private const int StyleHeader = 1, StyleDate = 2, StyleDateTime = 3, StyleDateTimeMs = 4;

    private static readonly DateTime FirstExcelDate = new(1900, 3, 1);
    private static readonly string[] DateFormats = { "yyyy-MM-dd" };
    private static readonly string[] DateTimeFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm" };

    /// <summary>Writes <paramref name="data"/> as a workbook with one sheet. NULL (a null row value) is an empty cell.</summary>
    public static XlsxWriteResult Write(Stream output, ResultGridData data, string sheetName = "Results")
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (data == null) throw new ArgumentNullException(nameof(data));

        int columns = data.ColumnNames?.Length ?? 0;
        if (columns > MaxColumns) throw new ArgumentException($"Excel allows {MaxColumns:N0} columns; the data has {columns:N0}.");
        if (data.Rows.Count + 1L > MaxRows) throw new ArgumentException($"Excel allows {MaxRows - 1:N0} rows below the header; the data has {data.Rows.Count:N0}.");

        var kinds = new CellKind[columns];
        for (int c = 0; c < columns; c++)
            kinds[c] = KindOf(data.SqlTypes != null && c < data.SqlTypes.Length ? data.SqlTypes[c] : null);

        var result = new XlsxWriteResult { Rows = data.Rows.Count, Columns = columns };
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", ContentTypes);
            WriteEntry(zip, "_rels/.rels", RootRels);
            WriteEntry(zip, "xl/workbook.xml", Workbook(SafeSheetName(sheetName)));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
            WriteEntry(zip, "xl/styles.xml", Styles);

            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            WriteSheet(xml, data, kinds, result);
        }
        return result;
    }

    private static void WriteSheet(XmlWriter xml, ResultGridData data, CellKind[] kinds, XlsxWriteResult result)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        int columns = kinds.Length;
        long lastRow = data.Rows.Count + 1;

        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", ns);

        if (columns > 0)
        {
            xml.WriteStartElement("dimension", ns);
            xml.WriteAttributeString("ref", "A1:" + ColumnLetters(columns - 1) + lastRow.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        // Freeze the header row.
        xml.WriteStartElement("sheetViews", ns);
        xml.WriteStartElement("sheetView", ns);
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane", ns);
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();

        if (columns > 0)
        {
            xml.WriteStartElement("cols", ns);
            for (int c = 0; c < columns; c++)
            {
                xml.WriteStartElement("col", ns);
                string n = (c + 1).ToString(CultureInfo.InvariantCulture);
                xml.WriteAttributeString("min", n);
                xml.WriteAttributeString("max", n);
                xml.WriteAttributeString("width", ColumnWidth(data, c, kinds[c]).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData", ns);

        xml.WriteStartElement("row", ns);
        xml.WriteAttributeString("r", "1");
        for (int c = 0; c < columns; c++)
        {
            string name = data.ColumnNames[c];
            WriteText(xml, ns, CellRef(c, 1), string.IsNullOrEmpty(name) ? "(No column name)" : name, StyleHeader, result);
        }
        xml.WriteEndElement();

        long r = 1;
        foreach (string[] row in data.Rows)
        {
            r++;
            xml.WriteStartElement("row", ns);
            xml.WriteAttributeString("r", r.ToString(CultureInfo.InvariantCulture));
            for (int c = 0; c < columns; c++)
            {
                string value = row != null && c < row.Length ? row[c] : null;
                if (value == null) continue;    // NULL: no cell at all
                WriteCell(xml, ns, CellRef(c, r), value, kinds[c], result);
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement(); // sheetData

        if (columns > 0)
        {
            xml.WriteStartElement("autoFilter", ns);
            xml.WriteAttributeString("ref", "A1:" + ColumnLetters(columns - 1) + lastRow.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // worksheet
        xml.WriteEndDocument();
    }

    private static void WriteCell(XmlWriter xml, string ns, string cellRef, string value, CellKind kind, XlsxWriteResult result)
    {
        switch (kind)
        {
            case CellKind.Number:
            case CellKind.ExactNumber:
                if (TryNumber(value, kind == CellKind.ExactNumber, out string number))
                {
                    WriteValue(xml, ns, cellRef, number, 0);
                    return;
                }
                break;
            case CellKind.Date:
            case CellKind.DateTime:
            case CellKind.DateTimeMs:
                if (TryDate(value, kind, out string serial))
                {
                    WriteValue(xml, ns, cellRef, serial, kind == CellKind.Date ? StyleDate : kind == CellKind.DateTime ? StyleDateTime : StyleDateTimeMs);
                    return;
                }
                break;
        }
        WriteText(xml, ns, cellRef, value, 0, result);
    }

    private static void WriteValue(XmlWriter xml, string ns, string cellRef, string value, int style)
    {
        xml.WriteStartElement("c", ns);
        xml.WriteAttributeString("r", cellRef);
        if (style != 0) xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteElementString("v", ns, value);
        xml.WriteEndElement();
    }

    private static void WriteText(XmlWriter xml, string ns, string cellRef, string value, int style, XlsxWriteResult result)
    {
        if (value.Length > MaxCellText)
        {
            // Do not cut a surrogate pair in half: the lone high surrogate left behind is not valid XML.
            int cut = char.IsHighSurrogate(value[MaxCellText - 1]) ? MaxCellText - 1 : MaxCellText;
            value = value.Substring(0, cut);
            result.TruncatedCells++;
        }

        xml.WriteStartElement("c", ns);
        xml.WriteAttributeString("r", cellRef);
        if (style != 0) xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("t", "inlineStr");
        xml.WriteStartElement("is", ns);
        xml.WriteStartElement("t", ns);
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(EscapeText(value));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>
    /// The SQL type's cell kind. Anything not recognised — character types, <c>uniqueidentifier</c>, <c>time</c>,
    /// <c>datetimeoffset</c>, <c>sql_variant</c>, no type at all — is text.
    /// </summary>
    internal static CellKind KindOf(string sqlType)
    {
        if (string.IsNullOrWhiteSpace(sqlType)) return CellKind.Text;
        string t = sqlType.Trim().ToLowerInvariant();
        int paren = t.IndexOf('(');
        string baseType = (paren >= 0 ? t.Substring(0, paren) : t).Trim();

        switch (baseType)
        {
            case "float":
            case "real":
                return CellKind.Number;
            case "bit":
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "decimal":
            case "numeric":
            case "money":
            case "smallmoney":
                return CellKind.ExactNumber;
            case "date":
                return CellKind.Date;
            case "smalldatetime":
                return CellKind.DateTime;
            case "datetime":
                return CellKind.DateTimeMs;
            case "datetime2":
                // datetime2(0) has no fraction to show; the default scale is 7.
                return paren >= 0 && t.Substring(paren).Trim('(', ')', ' ') == "0" ? CellKind.DateTime : CellKind.DateTimeMs;
            default:
                return CellKind.Text;
        }
    }

    /// <summary>
    /// The grid's text as an invariant-culture number for <c>&lt;v&gt;</c>, or false to keep it as text. Invariant is tried
    /// first, without thousands separators: the grid renders numerics as <c>1234.5600</c>, and an invariant parse that
    /// allowed separators would read a comma-decimal <c>1,5</c> as 15.
    /// </summary>
    internal static bool TryNumber(string text, bool exact, out string number)
    {
        number = null;
        string s = text.Trim();
        if (s.Length == 0) return false;

        if (exact)
        {
            const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            if (!decimal.TryParse(s, style, CultureInfo.InvariantCulture, out decimal d) && !decimal.TryParse(s, style, CultureInfo.CurrentCulture, out d))
                return false;
            if (SignificantDigits(d) > 15) return false;
            number = d.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        const NumberStyles floatStyle = NumberStyles.Float;
        if (!double.TryParse(s, floatStyle, CultureInfo.InvariantCulture, out double v) && !double.TryParse(s, floatStyle, CultureInfo.CurrentCulture, out v))
            return false;
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        number = v.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Significant digits of the value itself, ignoring leading zeros and the trailing zeros of the scale.</summary>
    private static int SignificantDigits(decimal d)
    {
        string text = Math.Abs(d).ToString(CultureInfo.InvariantCulture);
        string digits = text.Replace(".", "").TrimStart('0');
        if (text.IndexOf('.') >= 0)
            digits = digits.TrimEnd('0');
        return digits.Length;
    }

    /// <summary>The grid's date text as an Excel serial number, or false to keep it as text.</summary>
    internal static bool TryDate(string text, CellKind kind, out string serial)
    {
        serial = null;
        string s = text.Trim();
        string[] formats = kind == CellKind.Date ? DateFormats : DateTimeFormats;
        if (!DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value))
            return false;
        if (value < FirstExcelDate) return false;
        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0) return false;

        serial = value.ToOADate().ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Text made safe for a cell. XML 1.0 cannot carry most control characters at all, so they go out as Excel's
    /// <c>_xHHHH_</c> escape — which means a literal <c>_xHHHH_</c> already in the text must have its underscore escaped
    /// (<c>_x005F_</c>), or Excel would decode it. A lone surrogate becomes U+FFFD.
    /// </summary>
    internal static string EscapeText(string value)
    {
        StringBuilder sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            string replacement = null;

            if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' || ch == '￾' || ch == '￿')
                replacement = "_x" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture) + "_";
            else if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb?.Append(ch).Append(value[i + 1]);
                    i++;
                    continue;
                }
                replacement = "�";
            }
            else if (char.IsLowSurrogate(ch))
                replacement = "�";
            else if (ch == '_' && IsEscapeAt(value, i))
                replacement = "_x005F_";

            if (replacement != null)
            {
                sb ??= new StringBuilder(value, 0, i, value.Length + 16);
                sb.Append(replacement);
            }
            else
                sb?.Append(ch);
        }
        return sb?.ToString() ?? value;
    }

    private static bool IsEscapeAt(string s, int i)
    {
        if (i + 6 >= s.Length || s[i + 1] != 'x' || s[i + 6] != '_') return false;
        for (int k = i + 2; k < i + 6; k++)
            if (!Uri.IsHexDigit(s[k])) return false;
        return true;
    }

    /// <summary>Zero-based column index to Excel letters: 0 → A, 25 → Z, 26 → AA.</summary>
    internal static string ColumnLetters(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    private static string CellRef(int column, long row) => ColumnLetters(column) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>A width from the header and the first rows' text, between 8 and 60 characters. Dates get a fixed width.</summary>
    private static int ColumnWidth(ResultGridData data, int c, CellKind kind)
    {
        int width = Math.Max(8, (data.ColumnNames[c]?.Length ?? 16) + 4);  // room for the autofilter button
        if (kind == CellKind.Date) return Math.Max(width, 12);
        if (kind == CellKind.DateTime) return Math.Max(width, 20);
        if (kind == CellKind.DateTimeMs) return Math.Max(width, 24);

        int sampled = 0;
        foreach (string[] row in data.Rows)
        {
            if (++sampled > 200) break;
            string v = row != null && c < row.Length ? row[c] : null;
            if (v != null) width = Math.Max(width, Math.Min(v.Length, 60) + 2);
        }
        return Math.Min(width, 60);
    }

    /// <summary>Excel's sheet-name rules: 1–31 characters, none of <c>: \ / ? * [ ]</c>, not starting or ending with an apostrophe.</summary>
    internal static string SafeSheetName(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name ?? "")
            sb.Append(ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '_' : ch);
        string s = sb.ToString().Trim().Trim('\'');
        if (s.Length > 31) s = s.Substring(0, 31);
        return s.Length == 0 ? "Results" : s;
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Workbook(string sheetName)
    {
        var sb = new StringBuilder();
        using (var xml = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            xml.WriteStartElement("workbook", ns);
            xml.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            xml.WriteStartElement("sheets", ns);
            xml.WriteStartElement("sheet", ns);
            xml.WriteAttributeString("name", sheetName);
            xml.WriteAttributeString("sheetId", "1");
            xml.WriteAttributeString("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId1");
            xml.WriteEndElement();
            xml.WriteEndElement();
            // The autofilter needs its hidden defined name, or Excel drops the filter buttons on some builds.
            xml.WriteStartElement("definedNames", ns);
            xml.WriteStartElement("definedName", ns);
            xml.WriteAttributeString("name", "_xlnm._FilterDatabase");
            xml.WriteAttributeString("localSheetId", "0");
            xml.WriteAttributeString("hidden", "1");
            xml.WriteString("'" + sheetName.Replace("'", "''") + "'!$A$1");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + sb;
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private const string RootRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorkbookRels =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    // cellXfs: 0 default, 1 bold header, 2 date, 3 date and time, 4 date and time with milliseconds.
    private const string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"3\">" +
        "<numFmt numFmtId=\"164\" formatCode=\"yyyy\\-mm\\-dd\"/>" +
        "<numFmt numFmtId=\"165\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss\"/>" +
        "<numFmt numFmtId=\"166\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm:ss.000\"/>" +
        "</numFmts>" +
        "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"5\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";
}
