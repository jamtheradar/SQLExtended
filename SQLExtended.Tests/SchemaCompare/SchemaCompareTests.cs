using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLExtended.SchemaCompare.Engine;
using Xunit;

namespace SQLExtended.Tests.SchemaCompare;

/// <summary>
/// The SMO export and the SSDT project write the same schema in different shapes. These pin that the default options
/// read the two shapes as identical, and that every option still lets a real difference through.
/// </summary>
public class SchemaCompareTests
{
    // dbo.FeeEstimate as SchemaExportService (SMO) writes it, cut down.
    private const string SmoTable = @"SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[FeeEstimate](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[QuoteId] [int] NOT NULL,
	[MedicareNumber] [varchar](50) COLLATE Latin1_General_CI_AS NULL,
	[TotalTreatmentFee] [money] NULL,
	[Score] [float] NULL,
	[CreatedOn] [datetime2](7) NOT NULL,
 CONSTRAINT [PK_FeeEstimate] PRIMARY KEY CLUSTERED
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

SET ANSI_PADDING ON
GO

CREATE NONCLUSTERED INDEX [FeeEstimate_QuoteId] ON [dbo].[FeeEstimate]
(
	[QuoteId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
GO

ALTER TABLE [dbo].[FeeEstimate] ADD  DEFAULT ((0)) FOR [TotalTreatmentFee]
GO

ALTER TABLE [dbo].[FeeEstimate]  WITH CHECK ADD  CONSTRAINT [FK_FeeEstimate_Quote] FOREIGN KEY([QuoteId])
REFERENCES [dbo].[Quote] ([Id])
GO

ALTER TABLE [dbo].[FeeEstimate] CHECK CONSTRAINT [FK_FeeEstimate_Quote]
GO
";

    // The same table as an SSDT project imports it.
    private const string ProjectTable = @"CREATE TABLE [dbo].[FeeEstimate] (
    [Id]                INT           IDENTITY (1, 1) NOT NULL,
    [QuoteId]           INT           NOT NULL,
    [MedicareNumber]    VARCHAR (50)  NULL,
    [TotalTreatmentFee] MONEY         DEFAULT ((0)) NULL,
    [Score]             FLOAT (53)    NULL,
    [CreatedOn]         DATETIME2 (7) NOT NULL,
    CONSTRAINT [PK_FeeEstimate] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_FeeEstimate_Quote] FOREIGN KEY ([QuoteId]) REFERENCES [dbo].[Quote] ([Id])
);


GO
CREATE NONCLUSTERED INDEX [FeeEstimate_QuoteId]
    ON [dbo].[FeeEstimate]([QuoteId] ASC);
";

    private static SchemaSet Set(params (string Path, string Text)[] scripts)
        => ObjectExtractor.FromScripts(scripts.Select(s => new KeyValuePair<string, string>(s.Path, s.Text)));

    private static List<CompareItem> Compare(SchemaSet left, SchemaSet right, CompareOptions options = null)
        => SchemaComparer.Compare(left, right, options ?? new CompareOptions());

    private static CompareItem Single(List<CompareItem> items, string name) => Assert.Single(items, i => i.DisplayName == name);

    [Fact]
    public void SmoExport_And_ProjectTable_AreIdentical_WithDefaultOptions()
    {
        var items = Compare(Set(("Tables/dbo.FeeEstimate.sql", SmoTable)), Set(("dbo/Tables/FeeEstimate.sql", ProjectTable)));

        var item = Single(items, "dbo.FeeEstimate");
        Assert.True(item.Status == CompareStatus.Identical, item.LeftText + "\n----\n" + item.RightText);
    }

    [Fact]
    public void DefaultsWrittenSeparately_GoBackOnTheirColumn()
    {
        var items = Compare(Set(("a.sql", SmoTable)), Set(("b.sql", ProjectTable)));

        Assert.Contains("[TotalTreatmentFee] MONEY DEFAULT 0 NULL", Single(items, "dbo.FeeEstimate").LeftText);
    }

    [Theory]
    [InlineData(nameof(CompareOptions.IgnoreCollation))]
    [InlineData(nameof(CompareOptions.IgnoreDefaultIndexOptions))]
    [InlineData(nameof(CompareOptions.IgnoreDefaultFilegroup))]
    public void EachTableOption_WhenOff_LetsItsDifferenceThrough(string option)
    {
        var options = new CompareOptions();
        typeof(CompareOptions).GetProperty(option).SetValue(options, false);

        var items = Compare(Set(("a.sql", SmoTable)), Set(("b.sql", ProjectTable)), options);

        Assert.Equal(CompareStatus.Different, Single(items, "dbo.FeeEstimate").Status);
    }

    [Fact]
    public void RedundantParentheses_InDefaultsAndChecks()
    {
        // SQL Server stores what it was given wrapped: ((0)), (getdate()), ([Qty]>(0)). A hand-written project says 0.
        var left = Set(("a.sql", "CREATE TABLE dbo.T (Qty INT NULL DEFAULT ((0)), At DATETIME NULL DEFAULT (getdate()), CONSTRAINT CK_T CHECK (([Qty]>(0) AND [Qty]<=(100))))"));
        var right = Set(("b.sql", "CREATE TABLE dbo.T (Qty INT NULL DEFAULT 0, At DATETIME NULL DEFAULT getdate(), CONSTRAINT CK_T CHECK (Qty > 0 AND Qty <= 100))"));

        var item = Single(Compare(left, right), "dbo.T");
        Assert.True(item.Status == CompareStatus.Identical, item.LeftText + "\n----\n" + item.RightText);
        Assert.Equal(CompareStatus.Different, Single(Compare(left, right, new CompareOptions { IgnoreRedundantParentheses = false }), "dbo.T").Status);
    }

    [Fact]
    public void SessionSettings_WhenNotIgnored_AreADifference()
    {
        var items = Compare(Set(("a.sql", SmoTable)), Set(("b.sql", ProjectTable)), new CompareOptions { IgnoreSessionSettings = false });

        Assert.Equal(CompareStatus.Different, Single(items, "dbo.FeeEstimate").Status);
    }

    [Fact]
    public void NonDefaultIndexOption_IsStillADifference()
    {
        string withFillFactor = ProjectTable.Replace("([QuoteId] ASC);", "([QuoteId] ASC) WITH (FILLFACTOR = 80);");

        var items = Compare(Set(("a.sql", SmoTable)), Set(("b.sql", withFillFactor)));

        var item = Single(items, "dbo.FeeEstimate");
        Assert.Equal(CompareStatus.Different, item.Status);
        Assert.Contains("FILLFACTOR = 80", item.RightText);
    }

    [Fact]
    public void DisabledConstraint_IsADifference()
    {
        string disabled = SmoTable.Replace("ALTER TABLE [dbo].[FeeEstimate] CHECK CONSTRAINT [FK_FeeEstimate_Quote]", "ALTER TABLE [dbo].[FeeEstimate] NOCHECK CONSTRAINT [FK_FeeEstimate_Quote]");

        var items = Compare(Set(("a.sql", disabled)), Set(("b.sql", ProjectTable)));

        var item = Single(items, "dbo.FeeEstimate");
        Assert.Equal(CompareStatus.Different, item.Status);
        Assert.Contains("NOCHECK CONSTRAINT [FK_FeeEstimate_Quote]", item.LeftText);
    }

    [Fact]
    public void UntrustedConstraint_IsADifference_OnlyWhenTrustIsCompared()
    {
        string untrusted = SmoTable.Replace("WITH CHECK ADD  CONSTRAINT [FK_FeeEstimate_Quote]", "WITH NOCHECK ADD  CONSTRAINT [FK_FeeEstimate_Quote]");

        Assert.Equal(CompareStatus.Identical, Single(Compare(Set(("a.sql", untrusted)), Set(("b.sql", ProjectTable))), "dbo.FeeEstimate").Status);
        Assert.Equal(CompareStatus.Different, Single(Compare(Set(("a.sql", untrusted)), Set(("b.sql", ProjectTable)), new CompareOptions { IgnoreConstraintTrust = false }), "dbo.FeeEstimate").Status);
    }

    [Fact]
    public void SystemNamedConstraint_MatchesUnnamedOne()
    {
        var left = Set(("a.sql", "CREATE TABLE dbo.T ([Id] INT NOT NULL, CONSTRAINT [PK__T__3214EC07A1B2C3D4] PRIMARY KEY CLUSTERED ([Id] ASC))"));
        var right = Set(("b.sql", "CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY)"));

        Assert.Equal(CompareStatus.Identical, Single(Compare(left, right), "dbo.T").Status);
        Assert.Equal(CompareStatus.Different, Single(Compare(left, right, new CompareOptions { IgnoreSystemNames = false }), "dbo.T").Status);
    }

    [Fact]
    public void ColumnOrder_IsADifference_UnlessIgnored()
    {
        var left = Set(("a.sql", "CREATE TABLE dbo.T (A INT NULL, B INT NULL)"));
        var right = Set(("b.sql", "CREATE TABLE dbo.T (B INT NULL, A INT NULL)"));

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right), "dbo.T").Status);
        Assert.Equal(CompareStatus.Identical, Single(Compare(left, right, new CompareOptions { IgnoreColumnOrder = true }), "dbo.T").Status);
    }

    [Fact]
    public void Procedure_CommentsBracketsAndCreateOrAlter_AreIgnoredByDefault()
    {
        var left = Set(("a.sql", "SET ANSI_NULLS ON\r\nGO\r\nCREATE PROCEDURE [dbo].[GetThing] @Id INT\r\nAS\r\n-- the thing\r\nSELECT [Name] FROM [dbo].[Thing] WHERE Id = @Id;\r\nGO"));
        var right = Set(("b.sql", "CREATE OR ALTER PROCEDURE dbo.GetThing @Id int\r\nAS\r\n\r\n    select Name   from dbo.Thing where Id = @Id\r\n"));

        var items = Compare(left, right);
        var item = Single(items, "dbo.GetThing");
        Assert.True(item.Status == CompareStatus.Identical, item.LeftText + "\n----\n" + item.RightText);

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right, new CompareOptions { IgnoreComments = false }), "dbo.GetThing").Status);
        Assert.Equal(CompareStatus.Different, Single(Compare(left, right, new CompareOptions { IgnoreCase = false }), "dbo.GetThing").Status);
    }

    [Fact]
    public void Procedure_RealEdit_IsADifference()
    {
        var left = Set(("a.sql", "CREATE PROCEDURE dbo.P AS SELECT 1"));
        var right = Set(("b.sql", "CREATE PROCEDURE dbo.P AS SELECT 2"));

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right), "dbo.P").Status);
    }

    [Fact]
    public void BracketsAroundReservedWords_AreKept()
    {
        Assert.Equal("[Order]", Normalizer.Unbracket("[Order]"));
        Assert.Equal("[My Table]", Normalizer.Unbracket("[My Table]"));
        Assert.Equal("Customer", Normalizer.Unbracket("[Customer]"));
    }

    [Fact]
    public void TriggerInsideTheTableFile_MatchesTriggerInItsOwnFile()
    {
        var left = Set(("Tables/dbo.T.sql", "CREATE TABLE dbo.T (Id INT NOT NULL)\r\nGO\r\nCREATE TRIGGER [dbo].[trT] ON [dbo].[T] AFTER INSERT AS SET NOCOUNT ON\r\nGO\r\nALTER TABLE [dbo].[T] ENABLE TRIGGER [trT]\r\nGO"));
        var right = Set(("dbo/Tables/T.sql", "CREATE TABLE dbo.T (Id INT NOT NULL)"), ("dbo/Triggers/trT.sql", "CREATE TRIGGER dbo.trT ON dbo.T AFTER INSERT AS SET NOCOUNT ON"));

        var items = Compare(left, right);

        var table = Single(items, "dbo.T");
        Assert.True(table.Status == CompareStatus.Identical, table.LeftText + "\n----\n" + table.RightText);
        var trigger = Single(items, "dbo.trT");
        Assert.Equal(SchemaObjectType.Trigger, trigger.Type);
        Assert.Equal(CompareStatus.Identical, trigger.Status);
    }

    [Fact]
    public void DisabledTrigger_IsADifference()
    {
        var left = Set(("a.sql", "CREATE TRIGGER dbo.trT ON dbo.T AFTER INSERT AS SET NOCOUNT ON\r\nGO\r\nALTER TABLE [dbo].[T] DISABLE TRIGGER [trT]"));
        var right = Set(("b.sql", "CREATE TRIGGER dbo.trT ON dbo.T AFTER INSERT AS SET NOCOUNT ON"));

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right), "dbo.trT").Status);
    }

    [Fact]
    public void ExtendedProperty_BelongsToItsTable_HoweverItIsWritten()
    {
        var left = Set(("a.sql", "CREATE TABLE dbo.T (Id INT NULL)\r\nGO\r\nEXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'The id' , @level0type=N'SCHEMA',@level0name=N'dbo', @level1type=N'TABLE',@level1name=N'T', @level2type=N'COLUMN',@level2name=N'Id'"));
        var right = Set(("b.sql", "CREATE TABLE dbo.T (Id INT NULL)"), ("c.sql", "EXECUTE sp_addextendedproperty 'MS_Description', 'The id', 'SCHEMA', 'dbo', 'TABLE', 'T', 'COLUMN', 'Id'"));

        var items = Compare(left, right);

        Assert.Single(items);
        Assert.Equal(CompareStatus.Identical, Single(items, "dbo.T").Status);
    }

    [Fact]
    public void ExtendedProperties_CanBeIgnored()
    {
        var left = Set(("a.sql", "CREATE TABLE dbo.T (Id INT NULL)\r\nGO\r\nEXEC sp_addextendedproperty N'MS_Description', N'x', N'SCHEMA', N'dbo', N'TABLE', N'T'"));
        var right = Set(("b.sql", "CREATE TABLE dbo.T (Id INT NULL)"));

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right), "dbo.T").Status);
        Assert.Equal(CompareStatus.Identical, Single(Compare(left, right, new CompareOptions { IgnoreExtendedProperties = true }), "dbo.T").Status);
    }

    [Fact]
    public void EquivalentNames_ReadTheRightNameAsTheLeft()
    {
        var left = Set(("a.sql", "CREATE VIEW dbo.V AS SELECT Id FROM [GENGER.Patient].dbo.Patient"));
        var right = Set(("b.sql", "CREATE VIEW dbo.V AS SELECT Id FROM [GENGER-uat.Patient].dbo.Patient"));

        Assert.Equal(CompareStatus.Different, Single(Compare(left, right), "dbo.V").Status);
        Assert.Equal(CompareStatus.Identical, Single(Compare(left, right, new CompareOptions { EquivalentNames = "GENGER.Patient = GENGER-uat.Patient" }), "dbo.V").Status);
    }

    [Fact]
    public void SqlCmdVariable_InAProject_ParsesAndCanBeMapped()
    {
        var left = Set(("a.sql", "CREATE VIEW dbo.V AS SELECT Id FROM [GENGER.Patient].dbo.Patient"));
        var right = Set(("b.sql", "CREATE VIEW dbo.V AS SELECT Id FROM [$(PatientDb)].dbo.Patient"));

        var item = Single(Compare(left, right, new CompareOptions { EquivalentNames = "GENGER.Patient = $(PatientDb)" }), "dbo.V");

        Assert.True(item.Status == CompareStatus.Identical, item.LeftText + "\n----\n" + item.RightText);
    }

    [Fact]
    public void ExcludedObjects_AndSecurity_AreLeftOut()
    {
        var left = Set(("a.sql", "CREATE TABLE HangFire.Job (Id INT NULL)"), ("b.sql", "CREATE TABLE dbo.Keep (Id INT NULL)"));
        var right = Set(("c.sql", "CREATE USER [app] WITHOUT LOGIN"), ("d.sql", "ALTER ROLE [db_owner] ADD MEMBER [app]"));

        var items = Compare(left, right, new CompareOptions { ExcludeObjects = "HangFire.*" });

        Assert.Equal(new[] { "dbo.Keep" }, items.Select(i => i.DisplayName));
    }

    [Fact]
    public void UnparsableBatch_IsStillMatchedByItsHeader()
    {
        var left = Set(("a.sql", "CREATE PROCEDURE dbo.Broken AS SELECT FROM WHERE"));
        var right = Set(("b.sql", "CREATE PROCEDURE dbo.Broken AS SELECT FROM WHERE"));

        Assert.Equal(CompareStatus.Identical, Single(Compare(left, right), "dbo.Broken").Status);
        Assert.NotEmpty(left.Warnings);
    }

    // ---- LineDiff ---------------------------------------------------------------------------------------------

    [Fact]
    public void LineDiff_PairsChangedLines_AndMarksTheChangedWord()
    {
        var diff = LineDiff.Compute("a\r\nSELECT 1 FROM T\r\nc", "a\r\nSELECT 2 FROM T\r\nc", ignoreCase: false, ignoreWhitespace: false);

        Assert.Equal(new[] { DiffRowKind.Equal, DiffRowKind.Changed, DiffRowKind.Equal }, diff.Rows.Select(r => r.Kind));
        var span = Assert.Single(diff.Rows[1].LeftSpans);
        Assert.Equal("1", "SELECT 1 FROM T".Substring(span.Start, span.Length));
        Assert.Equal(new[] { 1 }, diff.HunkStarts);
    }

    [Fact]
    public void LineDiff_InsertedLines_GetFillerOnTheOtherSide()
    {
        var diff = LineDiff.Compute("a\nd", "a\nb\nc\nd", ignoreCase: false, ignoreWhitespace: false);

        Assert.Equal(new[] { DiffRowKind.Equal, DiffRowKind.RightOnly, DiffRowKind.RightOnly, DiffRowKind.Equal }, diff.Rows.Select(r => r.Kind));
        Assert.All(diff.Rows.Where(r => r.Kind == DiffRowKind.RightOnly), r => Assert.Equal(-1, r.Left));
    }

    [Fact]
    public void LineDiff_IgnoreCaseAndWhitespace_DecideEquality()
    {
        Assert.True(LineDiff.AreEqual("SELECT  x\n\nFROM t", "select x\nfrom t", ignoreCase: true, ignoreWhitespace: true));
        Assert.False(LineDiff.AreEqual("SELECT x", "select x", ignoreCase: false, ignoreWhitespace: true));
    }

    // ---- SourceLoader ------------------------------------------------------------------------------------------

    [Fact]
    public void Project_ReadsBuildItems_AndSkipsDeploymentScripts()
    {
        string root = Path.Combine(Path.GetTempPath(), "SchemaCompareTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dbo", "Tables"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "dbo", "Tables", "T.sql"), "CREATE TABLE dbo.T (Id INT NULL)");
            File.WriteAllText(Path.Combine(root, "dbo", "Tables", "NotInProject.sql"), "CREATE TABLE dbo.X (Id INT NULL)");
            File.WriteAllText(Path.Combine(root, "Script.PostDeployment.sql"), "INSERT dbo.T VALUES (1)");
            File.WriteAllText(Path.Combine(root, "bin", "Db.sql"), "CREATE TABLE dbo.Y (Id INT NULL)");
            File.WriteAllText(Path.Combine(root, "Db.sqlproj"), @"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
    <Build Include=""dbo\Tables\T.sql"" />
    <PostDeploy Include=""Script.PostDeployment.sql"" />
  </ItemGroup>
</Project>");

            var source = SourceLoader.Resolve(root);

            Assert.Equal(SourceKind.DatabaseProject, source.Kind);
            Assert.Equal(new[] { Path.Combine(root, "dbo", "Tables", "T.sql") }, source.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Folder_WithTypeFolders_IsAnExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "SchemaCompareTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Tables"));
            File.WriteAllText(Path.Combine(root, "Tables", "dbo.T.sql"), "CREATE TABLE dbo.T (Id INT NULL)");
            File.WriteAllText(Path.Combine(root, "Tables", "dbo.T.sqlproj.bak"), "not sql");

            var source = SourceLoader.Resolve(root);

            Assert.Equal(SourceKind.Export, source.Kind);
            Assert.Single(source.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
