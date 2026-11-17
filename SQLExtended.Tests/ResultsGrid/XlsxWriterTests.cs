using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using SQLExtended.ResultsGrid;
using Xunit;

namespace SQLExtended.Tests.ResultsGrid;

/// <summary>
/// Export to Excel's workbook writer. Every mistake it can make opens in Excel without complaint — a 007 that became 7, a
/// rounded 17-digit id, a datetime2 shown to the millisecond — so what is pinned here is how each value is typed.
/// </summary>
public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static ResultGridData Data(string[] names, string[] types, params string[][] rows)
    {
        var data = new ResultGridData { ColumnNames = names, SqlTypes = types };
        data.Rows.AddRange(rows);
        return data;
    }

    private static (XDocument sheet, XlsxWriteResult result, ZipArchive zip) Write(ResultGridData data)
    {
        var stream = new MemoryStream();
        var result = XlsxWriter.Write(stream, data);
        stream.Position = 0;
        var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml").Open();
        return (XDocument.Load(sheetStream), result, zip);
    }

    private static XElement Cell(XDocument sheet, string reference) =>
        sheet.Descendants(Main + "c").SingleOrDefault(c => (string)c.Attribute("r") == reference);

    private static string Text(XElement cell) => cell.Element(Main + "is").Element(Main + "t").Value;

    [Fact]
    public void AVarcharOfDigitsStaysText()
    {
        var (sheet, _, _) = Write(Data(new[] { "Code" }, new[] { "varchar(10)" }, new[] { "007" }));
        XElement cell = Cell(sheet, "A2");
        Assert.Equal("inlineStr", (string)cell.Attribute("t"));
        Assert.Equal("007", Text(cell));
    }

    [Fact]
    public void ExactNumericsAreNumbers()
    {
        var (sheet, _, _) = Write(Data(new[] { "Id", "Price" }, new[] { "int", "decimal(18,4)" }, new[] { "42", "-1234.5600" }));
        Assert.Null(Cell(sheet, "A2").Attribute("t"));
        Assert.Equal("42", Cell(sheet, "A2").Element(Main + "v").Value);
        Assert.Equal("-1234.5600", Cell(sheet, "B2").Element(Main + "v").Value);
    }

    [Fact]
    public void AnExactNumberBeyondFifteenDigitsIsKeptAsText()
    {
        // Excel keeps 15 significant digits: 1234567890123456789 would open as 1234567890123450000.
        var (sheet, _, _) = Write(Data(new[] { "Id" }, new[] { "bigint" }, new[] { "1234567890123456789" }, new[] { "123456789012345" }));
        Assert.Equal("1234567890123456789", Text(Cell(sheet, "A2")));
        Assert.Equal("123456789012345", Cell(sheet, "A3").Element(Main + "v").Value);
    }

    [Fact]
    public void TrailingZerosOfTheScaleAreNotSignificant()
    {
        Assert.True(XlsxWriter.TryNumber("12345678901.0000000", exact: true, out _));
        Assert.True(XlsxWriter.TryNumber("0.000000000000123", exact: true, out _));
    }

    [Fact]
    public void FloatsAreNumbersWhateverTheirLength()
    {
        Assert.True(XlsxWriter.TryNumber("1E+300", exact: false, out string big));
        Assert.Equal("1E+300", big);
        Assert.True(XlsxWriter.TryNumber("0.10000000000000001", exact: false, out _));
    }

    [Fact]
    public void ACommaIsNotReadAsAThousandsSeparator()
    {
        // An invariant parse with thousands allowed reads "1,5" as 15. Under a comma-decimal culture it is 1.5; under an
        // English one it does not parse and stays text. Never 15.
        bool parsed = XlsxWriter.TryNumber("1,5", exact: true, out string number);
        Assert.True(!parsed || number == "1.5");
    }

    [Fact]
    public void TextThatDoesNotParseInANumericColumnStaysText()
    {
        var (sheet, _, _) = Write(Data(new[] { "N" }, new[] { "int" }, new[] { "n/a" }));
        Assert.Equal("n/a", Text(Cell(sheet, "A2")));
    }

    [Fact]
    public void DatesAreSerialNumbersWithADateStyle()
    {
        var (sheet, _, _) = Write(Data(new[] { "D", "DT", "SDT" }, new[] { "date", "datetime", "smalldatetime" },
            new[] { "2026-11-17", "2026-11-17 09:45:12.123", "2026-11-17 09:45:00" }));

        Assert.Equal(new DateTime(2026, 11, 17).ToOADate(), double.Parse(Cell(sheet, "A2").Element(Main + "v").Value, CultureInfo.InvariantCulture));
        Assert.Equal("2", (string)Cell(sheet, "A2").Attribute("s"));
        Assert.Equal(new DateTime(2026, 11, 17, 9, 45, 12, 123).ToOADate(), double.Parse(Cell(sheet, "B2").Element(Main + "v").Value, CultureInfo.InvariantCulture));
        Assert.Equal("4", (string)Cell(sheet, "B2").Attribute("s"));
        Assert.Equal("3", (string)Cell(sheet, "C2").Attribute("s"));
    }

    [Fact]
    public void ADatetime2WithPrecisionBelowTheMillisecondStaysText()
    {
        var (sheet, _, _) = Write(Data(new[] { "T" }, new[] { "datetime2(7)" }, new[] { "2026-11-17 09:45:12.1234567" }, new[] { "2026-11-17 09:45:12.1230000" }));
        Assert.Equal("2026-11-17 09:45:12.1234567", Text(Cell(sheet, "A2")));
        Assert.Null(Cell(sheet, "A3").Attribute("t"));
    }

    [Fact]
    public void ADateBeforeMarch1900StaysText()
    {
        // Excel's calendar starts in 1900 and counts a 29 February that never was, so serials before March are off by one.
        var (sheet, _, _) = Write(Data(new[] { "D" }, new[] { "date" }, new[] { "1899-12-31" }, new[] { "1900-02-28" }));
        Assert.Equal("1899-12-31", Text(Cell(sheet, "A2")));
        Assert.Equal("1900-02-28", Text(Cell(sheet, "A3")));
    }

    [Fact]
    public void ANullIsNoCellAndAnEmptyStringIsAnEmptyTextCell()
    {
        var (sheet, _, _) = Write(Data(new[] { "A", "B" }, new[] { "varchar(5)", "varchar(5)" }, new[] { null, "" }));
        Assert.Null(Cell(sheet, "A2"));
        Assert.Equal("", Text(Cell(sheet, "B2")));
    }

    [Fact]
    public void TheHeaderIsBoldAndAnUnnamedColumnIsLabelled()
    {
        var (sheet, _, _) = Write(Data(new[] { "Name", "" }, new[] { "int", "int" }));
        Assert.Equal("Name", Text(Cell(sheet, "A1")));
        Assert.Equal("1", (string)Cell(sheet, "A1").Attribute("s"));
        Assert.Equal("(No column name)", Text(Cell(sheet, "B1")));
        Assert.Equal("A1:B1", (string)sheet.Descendants(Main + "autoFilter").Single().Attribute("ref"));
    }

    [Fact]
    public void ControlCharactersAreEscapedAndALiteralEscapeIsProtected()
    {
        Assert.Equal("a_x0001_b", XlsxWriter.EscapeText("a\u0001b"));
        Assert.Equal("tab\tand\r\nnewline", XlsxWriter.EscapeText("tab\tand\r\nnewline"));
        Assert.Equal("_x005F_x0041_", XlsxWriter.EscapeText("_x0041_"));
        Assert.Equal("_x_y", XlsxWriter.EscapeText("_x_y"));
        Assert.Equal("a�b", XlsxWriter.EscapeText("a\uD800b"));
        Assert.Equal("😀", XlsxWriter.EscapeText("😀"));
    }

    [Fact]
    public void AControlCharacterStillProducesAReadableSheet()
    {
        var (sheet, _, _) = Write(Data(new[] { "T" }, new[] { "nvarchar(max)" }, new[] { "bell\u0007" }));
        Assert.Equal("bell_x0007_", Text(Cell(sheet, "A2")));
    }

    [Fact]
    public void TextOverExcelsLimitIsCutAndCounted()
    {
        var (sheet, result, _) = Write(Data(new[] { "T" }, new[] { "nvarchar(max)" }, new[] { new string('x', 40000) }, new[] { "short" }));
        Assert.Equal(XlsxWriter.MaxCellText, Text(Cell(sheet, "A2")).Length);
        Assert.Equal(1, result.TruncatedCells);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    [InlineData(16383, "XFD")]
    public void ColumnLetters(int index, string expected) => Assert.Equal(expected, XlsxWriter.ColumnLetters(index));

    [Theory]
    [InlineData("int", "ExactNumber")]
    [InlineData("DECIMAL(18, 2)", "ExactNumber")]
    [InlineData("bit", "ExactNumber")]
    [InlineData("float", "Number")]
    [InlineData("date", "Date")]
    [InlineData("datetime2(0)", "DateTime")]
    [InlineData("datetime2(3)", "DateTimeMs")]
    [InlineData("datetime2", "DateTimeMs")]
    [InlineData("datetimeoffset(7)", "Text")]
    [InlineData("time(7)", "Text")]
    [InlineData("uniqueidentifier", "Text")]
    [InlineData(null, "Text")]
    public void KindOf(string sqlType, string expected) => Assert.Equal(expected, XlsxWriter.KindOf(sqlType).ToString());

    [Theory]
    [InlineData("Results", "Results")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("", "Results")]
    [InlineData("0123456789012345678901234567890123", "0123456789012345678901234567890")]
    public void SafeSheetName(string name, string expected) => Assert.Equal(expected, XlsxWriter.SafeSheetName(name));

    [Fact]
    public void EveryPartIsPresentAndWellFormed()
    {
        var (_, _, zip) = Write(Data(new[] { "A" }, new[] { "int" }, new[] { "1" }));
        string[] parts = { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" };
        foreach (string part in parts)
        {
            ZipArchiveEntry entry = zip.GetEntry(part);
            Assert.NotNull(entry);
            using var s = entry.Open();
            XDocument.Load(s);
        }
    }
}
