using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLExtended.SchemaExporter;
using Xunit;

namespace SQLExtended.Tests.SchemaExporter;

/// <summary>
/// The query half of SchemaExporter: how a result set becomes files. What matters is that the same rows always give
/// the same bytes (or a quiet night commits changes), that names can't escape the folder, and that the brakes hold.
/// </summary>
public sealed class QueryOutputTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "QueryOutputTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_target, recursive: true); } catch { }
    }

    private static readonly string[] JobColumns = { "Queue", "JobId", "Cron", "Enabled", "LastRun" };

    private static object[] Job(string queue, string id, string cron, bool enabled, object lastRun = null) =>
        new[] { queue, id, cron, enabled, lastRun ?? DBNull.Value };

    private static QueryOutputOptions PerRow(string fileName = "{Queue}/{JobId}") => new() { FileName = fileName };

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void FilePerRow_Json_WritesColumnsInQueryOrder_WithNullsAndTypes()
    {
        var files = QueryOutput.BuildFiles(PerRow(), "t", JobColumns,
            new[] { Job("default", "GenerateInvoices", "0 4 * * *", true, new DateTime(2026, 11, 19, 4, 0, 0)), Job("default", "Purge", "0 * * * *", false) }, null);

        Assert.Equal(new[] { @"default\GenerateInvoices.json", @"default\Purge.json" }, files.Keys.OrderBy(k => k));
        Assert.Equal(
            "{\r\n  \"Queue\": \"default\",\r\n  \"JobId\": \"Purge\",\r\n  \"Cron\": \"0 * * * *\",\r\n  \"Enabled\": false,\r\n  \"LastRun\": null\r\n}\r\n",
            Text(files[@"default\Purge.json"]));
        Assert.Contains("\"LastRun\": \"2026-11-19T04:00:00\"", Text(files[@"default\GenerateInvoices.json"]));
    }

    [Fact]
    public void Output_HasNoBom()
    {
        var files = QueryOutput.BuildFiles(PerRow(), "t", JobColumns, new[] { Job("q", "a", "c", true) }, null);
        Assert.NotEqual(0xEF, files.Values.Single()[0]);
    }

    [Fact]
    public void SameRows_GiveSameBytes()
    {
        object[][] rows = { Job("q", "a", "c", true, 1.5m) };
        var first = QueryOutput.BuildFiles(PerRow(), "t", JobColumns, rows, null);
        var second = QueryOutput.BuildFiles(PerRow(), "t", JobColumns, rows, null);
        Assert.Equal(first.Values.Single(), second.Values.Single());
    }

    [Fact]
    public void ExcludedColumns_StayOutOfContent_ButStillNameTheFile()
    {
        var options = PerRow("{JobId}");
        options.ExcludeColumns = new List<string> { "JobId", "LastRun" };

        var files = QueryOutput.BuildFiles(options, "t", JobColumns, new[] { Job("q", "a", "c", true) }, null);

        string json = Text(files["a.json"]);
        Assert.DoesNotContain("JobId", json);
        Assert.DoesNotContain("LastRun", json);
        Assert.Contains("\"Cron\"", json);
    }

    [Fact]
    public void DuplicateFileNames_FirstWins_AndWarns()
    {
        var warnings = new List<string>();
        var files = QueryOutput.BuildFiles(PerRow("{Queue}"), "t", JobColumns, new[] { Job("q", "a", "1", true), Job("Q", "b", "2", true) }, warnings.Add);

        Assert.Single(files);
        Assert.Contains("\"JobId\": \"a\"", Text(files["q.json"]));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("{Queue}/{JobId}", "a/b", "c", @"a_b\c.json")]        // a value can't add a folder
    [InlineData("{Queue}/{JobId}", "..", "c", @"_\c.json")]           // nor climb out of one
    [InlineData("{Queue}/{JobId}", "", "c", @"_\c.json")]             // empty becomes _
    [InlineData("{Queue}/{JobId}", "x:y", "c?", @"x_y\c_.json")]      // invalid characters
    [InlineData("{target}/{JobId}", "q", "c", @"SYDSQ09\c.json")]     // the target token
    [InlineData("{JobId}.json", "q", "c", @"c.json")]                 // extension not doubled
    public void ResolvePath_KeepsEveryNameInsideTheFolder(string template, string queue, string id, string expected)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Queue"] = queue, ["JobId"] = id };
        Assert.Equal(expected, QueryOutput.ResolvePath(template, ".json", "SYDSQ09", c => values[c]));
    }

    [Fact]
    public void SingleFile_Csv_QuotesWhereNeeded()
    {
        var options = new QueryOutputOptions { Mode = QueryExportMode.SingleFile, Format = QueryExportFormat.Csv, FileName = "jobs" };
        var files = QueryOutput.BuildFiles(options, "t", new[] { "Name", "Note" },
            new[] { new object[] { "a", "plain" }, new object[] { "b", "has, comma \"and quote\"" }, new object[] { "c", DBNull.Value } }, null);

        Assert.Equal("Name,Note\r\na,plain\r\nb,\"has, comma \"\"and quote\"\"\"\r\nc,\r\n", Text(files["jobs.csv"]));
    }

    [Fact]
    public void SingleFile_Json_IsAnArray()
    {
        var options = new QueryOutputOptions { Mode = QueryExportMode.SingleFile, FileName = "{target}" };
        var files = QueryOutput.BuildFiles(options, "SYDSQ09", new[] { "N" }, new[] { new object[] { 1 }, new object[] { 2 } }, null);

        Assert.Equal("[\r\n  {\r\n    \"N\": 1\r\n  },\r\n  {\r\n    \"N\": 2\r\n  }\r\n]\r\n", Text(files["SYDSQ09.json"]));
    }

    [Fact]
    public void Raw_WritesTheContentColumnAsIs()
    {
        var options = new QueryOutputOptions { Format = QueryExportFormat.Raw, FileName = "{Name}.sql", ContentColumn = "Body" };
        byte[] binary = { 1, 2, 3 };
        var files = QueryOutput.BuildFiles(options, "t", new[] { "Name", "Body" },
            new[] { new object[] { "a", "SELECT 1\r\n" }, new object[] { "b", binary } }, null);

        Assert.Equal("SELECT 1\r\n", Text(files["a.sql"]));
        Assert.Equal(binary, files["b.sql"]);
    }

    [Fact]
    public void Text_WritesKeyValueLines()
    {
        var options = new QueryOutputOptions { Format = QueryExportFormat.Text, FileName = "{Name}" };
        var files = QueryOutput.BuildFiles(options, "t", new[] { "Name", "Note" }, new[] { new object[] { "a", "line1\r\nline2" } }, null);

        Assert.Equal("Name: a\r\nNote: line1\\nline2\r\n", Text(files["a.txt"]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(true, "true")]
    [InlineData(12.50, "12.5")]
    public void FormatValue_IsInvariant(object value, string expected) => Assert.Equal(expected, QueryOutput.FormatValue(value));

    [Fact]
    public void FormatValue_DatesAndGuids()
    {
        Assert.Equal("2026-01-02T03:04:05", QueryOutput.FormatValue(new DateTime(2026, 1, 2, 3, 4, 5)));
        Assert.Equal("2026-01-02T03:04:05.123", QueryOutput.FormatValue(new DateTime(2026, 1, 2, 3, 4, 5, 123)));
        Assert.Equal("2026-01-02T03:04:05+11:00", QueryOutput.FormatValue(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(11))));
        Assert.Equal("1.5", QueryOutput.FormatValue(1.5m));
        Assert.Equal("00000000-0000-0000-0000-00000000000a", QueryOutput.FormatValue(new Guid("0000000000000000000000000000000A")));
        Assert.Equal("AQID", QueryOutput.FormatValue(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData(new[] { "Queue", "JobId" }, "{Queue}/{Nope}", "no column of that name")]
    [InlineData(new[] { "Queue", "Queue" }, "{Queue}", "more than once")]
    [InlineData(new[] { "Queue", "" }, "{Queue}", "has no name")]
    [InlineData(new[] { "Queue", "Target" }, "{Queue}", "reserved")]
    public void ValidateColumns_CatchesBadResultSets(string[] columns, string fileName, string expected)
    {
        string problem = QueryOutput.ValidateColumns(PerRow(fileName), columns);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void ValidateColumns_AcceptsAGoodResultSet() => Assert.Null(QueryOutput.ValidateColumns(PerRow(), JobColumns));

    [Fact]
    public void ValidateColumns_SingleFileNameCanOnlyUseTarget()
    {
        var options = new QueryOutputOptions { Mode = QueryExportMode.SingleFile, FileName = "{Queue}" };
        Assert.Contains("can only use", QueryOutput.ValidateColumns(options, JobColumns));
    }

    [Theory]
    [InlineData(10, 1, 25, false)]   // one file never brakes
    [InlineData(2, 2, 25, true)]
    [InlineData(10, 2, 25, false)]   // 20%
    [InlineData(10, 3, 25, true)]    // 30%
    [InlineData(10, 9, null, false)] // brake off
    [InlineData(0, 0, 25, false)]
    public void DeleteBrake(int existing, int deleted, int? max, bool expected) =>
        Assert.Equal(expected, QueryOutput.ExceedsDeleteBrake(existing, deleted, max));

    [Fact]
    public void CountDeletions_IgnoresGitAttributes()
    {
        Directory.CreateDirectory(Path.Combine(_target, "q"));
        File.WriteAllText(Path.Combine(_target, ReportMirror.GitAttributesFile), "* -text\n");
        File.WriteAllText(Path.Combine(_target, @"q\a.json"), "{}");
        File.WriteAllText(Path.Combine(_target, @"q\b.json"), "{}");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [@"q\a.json"] = new byte[0] };
        Assert.Equal((2, 1), QueryOutput.CountDeletions(_target, files));
    }
}
