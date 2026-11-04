using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SQLExtended.SchemaExporter;
using Xunit;

namespace SQLExtended.Tests.SchemaExporter;

/// <summary>
/// The SSRS half of SchemaExporter: where each catalog item lands, and that the report server's folder ends up
/// matching the catalog exactly — no more (deleted reports go), no less, and unchanged files left alone.
/// </summary>
public sealed class ReportMirrorTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "ReportMirrorTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_target, recursive: true); } catch { }
    }

    private static Dictionary<string, byte[]> Catalog(params (string Path, string Text)[] items)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in items) files.Add(path, Encoding.UTF8.GetBytes(text));
        return files;
    }

    [Theory]
    [InlineData("/Finance/Monthly Sales", 2, @"Finance\Monthly Sales.rdl")]
    [InlineData("/Data Sources/Warehouse", 5, @"Data Sources\Warehouse.rds")]
    [InlineData("/Shared/Customers", 8, @"Shared\Customers.rsd")]
    [InlineData("/Finance/logo.png", 3, @"Finance\logo.png")]
    [InlineData("/Budget.xlsx", 14, @"Budget.xlsx")]
    [InlineData("/Odd/Name.rdl", 2, @"Odd\Name.rdl")]
    [InlineData("/A/Trailing dot.", 2, @"A\Trailing dot.rdl")]
    public void FilePathFor_MapsCatalogPathAndType(string catalogPath, int type, string expected)
        => Assert.Equal(expected, ReportMirror.FilePathFor(catalogPath, type));

    [Theory]
    [InlineData("/Finance", 1)]   // folder
    [InlineData("/Finance/L", 4)] // linked report
    [InlineData("/Finance/P", 13)] // Power BI report
    [InlineData("", 2)]
    public void FilePathFor_SkipsItemsWithNoContentOfTheirOwn(string catalogPath, int type)
        => Assert.Null(ReportMirror.FilePathFor(catalogPath, type));

    [Fact]
    public void Apply_AddsChangesAndRemoves_LeavingUnchangedAndGitAttributesAlone()
    {
        ReportMirror.Apply(_target, Catalog((@"A\One.rdl", "1"), (@"A\Two.rdl", "2"), (@"B\Gone.rdl", "x")));
        File.WriteAllText(Path.Combine(_target, ReportMirror.GitAttributesFile), "* -text\n");
        var unchangedTime = new DateTime(2020, 1, 1);
        File.SetLastWriteTime(Path.Combine(_target, @"A\One.rdl"), unchangedTime);

        var result = ReportMirror.Apply(_target, Catalog((@"A\One.rdl", "1"), (@"A\Two.rdl", "2b"), (@"C\New.rsd", "n")));

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(unchangedTime, File.GetLastWriteTime(Path.Combine(_target, @"A\One.rdl")));
        Assert.Equal("2b", File.ReadAllText(Path.Combine(_target, @"A\Two.rdl")));
        Assert.False(Directory.Exists(Path.Combine(_target, "B")));
        Assert.True(File.Exists(Path.Combine(_target, ReportMirror.GitAttributesFile)));
    }

    [Fact]
    public void Apply_CaseOnlyRename_IsWrittenUnderTheNewName()
    {
        ReportMirror.Apply(_target, Catalog((@"A\Sales.rdl", "s")));

        var result = ReportMirror.Apply(_target, Catalog((@"A\SALES.rdl", "s")));

        Assert.Equal(1, result.Updated);
        Assert.Equal("SALES.rdl", Path.GetFileName(Directory.GetFiles(Path.Combine(_target, "A"))[0]));
    }

    [Theory]
    [InlineData("Data Source=PROD;Initial Catalog=DW;User ID=rpt;Password=s3cret", "s3cret")]
    [InlineData("Server=PROD;Database=DW;UID=rpt;PWD='a;b'", "a;b")]
    public void RedactPassword_MasksPasswordInTheConnectionStringItself(string connectString, string secret)
    {
        string redacted = ReportMirror.RedactPassword(connectString);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("********", redacted);
        Assert.Contains("PROD", redacted);
    }

    [Theory]
    [InlineData("Data Source=PROD;Initial Catalog=DW")] // kept exactly, not rebuilt
    [InlineData("data source = PROD ; initial catalog=DW;")]
    [InlineData("http://sharepoint/sites/finance/_vti_bin/lists.asmx")]
    [InlineData("")]
    public void RedactPassword_LeavesStringsWithoutAPasswordUntouched(string connectString)
        => Assert.Equal(connectString, ReportMirror.RedactPassword(connectString));

    [Fact]
    public void Apply_SecondRunWithNoChanges_ReportsNothing()
    {
        var catalog = Catalog((@"A\One.rdl", "1"), (@"logo.png", "png"));
        ReportMirror.Apply(_target, catalog);

        Assert.False(ReportMirror.Apply(_target, catalog).Changed);
    }
}
