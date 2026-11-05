using System;
using System.Collections.Generic;
using System.Linq;
using SQLExtended.SchemaCompare.Engine;
using Xunit;

namespace SQLExtended.Tests.SchemaCompare;

/// <summary>
/// The change script takes the target to the source, altering in place what can be altered and leaving the rest as
/// REVIEW comments. These pin what is scripted, and the order it runs in.
/// </summary>
public class ChangeScriptTests
{
    private static SchemaSet Set(params (string Path, string Text)[] scripts)
        => ObjectExtractor.FromScripts(scripts.Select(s => new KeyValuePair<string, string>(s.Path, s.Text)));

    private static ChangeScript Script(SchemaSet left, SchemaSet right, ScriptOptions script = null, CompareOptions options = null)
    {
        options ??= new CompareOptions();
        var items = SchemaComparer.Compare(left, right, options);
        return ChangeScripter.Generate(items, left, right, options, script ?? new ScriptOptions());
    }

    private static int At(string text, string find)
    {
        int i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        Assert.True(i >= 0, $"Expected to find: {find}\n\n{text}");
        return i;
    }

    private const string Customer = "CREATE TABLE [dbo].[Customer] ([Id] INT NOT NULL, [Name] NVARCHAR (50) NULL, CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC));";

    [Fact]
    public void IdenticalSides_ScriptNothing()
    {
        var script = Script(Set(("a.sql", Customer)), Set(("b.sql", Customer)));
        Assert.Contains("Nothing to change", script.Text);
        Assert.Equal(0, script.Created + script.Altered + script.Dropped);
    }

    [Fact]
    public void ChangedProcedure_BecomesCreateOrAlter_KeepingItsText()
    {
        var left = Set(("p.sql", "-- keep me\nCREATE PROCEDURE dbo.GetCustomer AS SELECT 2;"));
        var right = Set(("p.sql", "CREATE PROCEDURE dbo.GetCustomer AS SELECT 1;"));
        var script = Script(left, right);

        Assert.Contains("-- keep me\nCREATE OR ALTER PROCEDURE dbo.GetCustomer AS SELECT 2;", script.Text);
        Assert.Equal(1, script.Altered);
    }

    [Fact]
    public void AlterProcedure_AndCreateOrAlter_AreLeftAsCreateOrAlter()
    {
        Assert.Equal("CREATE OR ALTER VIEW v AS SELECT 1", ChangeScripter.CreateOrAlter("ALTER VIEW v AS SELECT 1"));
        Assert.Equal("/* x */ CREATE OR ALTER VIEW v AS SELECT 1", ChangeScripter.CreateOrAlter("/* x */ CREATE OR ALTER VIEW v AS SELECT 1"));
    }

    [Fact]
    public void NewTables_AreCreated_BeforeTheirForeignKeys()
    {
        var left = Set(
            ("Order.sql", "CREATE TABLE dbo.[Order] (Id INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer (Id));"),
            ("Customer.sql", Customer));
        var script = Script(left, Set()).Text;

        int fk = At(script, "ADD CONSTRAINT [FK_Order_Customer]");
        Assert.True(At(script, "CREATE TABLE [dbo].[Order]") < fk);
        Assert.True(At(script, "CREATE TABLE [dbo].[Customer]") < fk);
        Assert.DoesNotContain("FOREIGN KEY", script.Substring(0, fk));
    }

    [Fact]
    public void ColumnChanges_AreAlteredInPlace()
    {
        var left = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Name NVARCHAR(100) NULL, Added INT NULL);"));
        var right = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Name NVARCHAR(50) NULL, Gone INT NULL);"));
        var script = Script(left, right);

        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [Name] NVARCHAR (100) NULL", script.Text);
        Assert.Contains("ALTER TABLE [dbo].[T] ADD [Added] INT NULL", script.Text);
        Assert.Contains("ALTER TABLE [dbo].[T] DROP COLUMN [Gone]", script.Text);
        Assert.Contains(script.Warnings, w => w.Contains("[Gone] is dropped"));
        Assert.DoesNotContain("CREATE TABLE", script.Text);
    }

    [Fact]
    public void IndexOnAnAlteredColumn_IsDroppedFirst_AndPutBack()
    {
        var left = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Code VARCHAR(20) NULL);\nGO\nCREATE INDEX IX_T_Code ON dbo.T (Code);"));
        var right = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Code VARCHAR(10) NULL);\nGO\nCREATE INDEX IX_T_Code ON dbo.T (Code);"));
        var script = Script(left, right).Text;

        int drop = At(script, "DROP INDEX [IX_T_Code] ON [dbo].[T]");
        int alter = At(script, "ALTER COLUMN [Code]");
        int create = At(script, "NONCLUSTERED INDEX [IX_T_Code]");
        Assert.True(drop < alter && alter < create);
    }

    [Fact]
    public void ChangedUnnamedDefault_IsDroppedByLookingUpItsName()
    {
        var left = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Flag BIT NOT NULL DEFAULT 1);"));
        var right = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Flag BIT NOT NULL DEFAULT 0);"));
        var script = Script(left, right).Text;

        Assert.Contains("FROM sys.default_constraints", script);
        Assert.Contains("c.name = N'Flag'", script);
        Assert.Contains("ALTER TABLE [dbo].[T] ADD DEFAULT 1 FOR [Flag]", script);
    }

    [Fact]
    public void ObjectsOnlyOnTheTarget_AreDroppedOnlyWhenAsked()
    {
        var left = Set(("a.sql", Customer));
        var right = Set(("a.sql", Customer), ("old.sql", "CREATE VIEW dbo.Old AS SELECT 1 AS One;"));

        var kept = Script(left, right);
        Assert.DoesNotContain("DROP VIEW", kept.Text);
        Assert.Contains("View dbo.Old", kept.NotDropped);

        var dropped = Script(left, right, new ScriptOptions { IncludeDrops = true });
        Assert.Contains("DROP VIEW IF EXISTS [dbo].[Old]", dropped.Text);
        Assert.Equal(1, dropped.Dropped);
    }

    [Fact]
    public void RightToLeft_TakesTheRightAsTheSource()
    {
        var left = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL);"));
        var right = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL, Extra INT NULL);"));

        Assert.Contains("ADD [Extra] INT NULL", Script(left, right, new ScriptOptions { LeftToRight = false }).Text);
        Assert.Contains("DROP COLUMN [Extra]", Script(left, right, new ScriptOptions { LeftToRight = true }).Text);
    }

    [Fact]
    public void EquivalentNames_AreWrittenInTheTargetsNames()
    {
        var options = new CompareOptions { EquivalentNames = "GENGER.Patient = GENGER-uat.Patient" };
        var left = Set(("v.sql", "CREATE VIEW dbo.P AS SELECT Id, Name FROM [GENGER.Patient].dbo.Patient;"));
        var right = Set(("v.sql", "CREATE VIEW dbo.P AS SELECT Id FROM [GENGER-uat.Patient].dbo.Patient;"));
        var script = Script(left, right, options: options).Text;

        Assert.Contains("FROM [GENGER-uat.Patient].dbo.Patient", script);
        Assert.DoesNotContain("[GENGER.Patient]", script);
    }

    [Fact]
    public void RebuiltPrimaryKey_TakesItsReferencingForeignKeysWithIt()
    {
        const string order = "CREATE TABLE dbo.[Order] (Id INT NOT NULL, CustomerId INT NOT NULL, CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (Id));";
        var left = Set(("c.sql", "CREATE TABLE dbo.Customer (Id INT NOT NULL, CONSTRAINT PK_Customer PRIMARY KEY NONCLUSTERED (Id));"), ("o.sql", order));
        var right = Set(("c.sql", "CREATE TABLE dbo.Customer (Id INT NOT NULL, CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (Id));"), ("o.sql", order));
        var script = Script(left, right).Text;

        int dropFk = At(script, "ALTER TABLE [dbo].[Order] DROP CONSTRAINT [FK_Order_Customer]");
        int dropPk = At(script, "ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer]");
        int addPk = At(script, "ADD CONSTRAINT [PK_Customer] PRIMARY KEY NONCLUSTERED");
        int addFk = At(script, "ADD CONSTRAINT [FK_Order_Customer]");
        Assert.True(dropFk < dropPk && dropPk < addPk && addPk < addFk);
    }

    [Fact]
    public void GrantOnlyOnTheTarget_IsRevoked()
    {
        var options = new CompareOptions { IgnoreSecurity = false };
        var left = Set(("p.sql", "GRANT SELECT ON dbo.Customer TO Reader;"));
        var right = Set(("p.sql", "GRANT SELECT ON dbo.Customer TO Reader;\nGO\nGRANT DELETE ON dbo.Customer TO Reader;"));
        var script = Script(left, right, options: options).Text;

        Assert.Contains("REVOKE DELETE", script);
        Assert.DoesNotContain("REVOKE SELECT", script);
    }

    [Fact]
    public void ChangedType_IsLeftForReview_NotDropped()
    {
        var left = Set(("t.sql", "CREATE TYPE dbo.Ids AS TABLE (Id BIGINT NOT NULL);"));
        var right = Set(("t.sql", "CREATE TYPE dbo.Ids AS TABLE (Id INT NOT NULL);"));
        var script = Script(left, right);

        Assert.Contains("-- REVIEW:", script.Text);
        Assert.DoesNotContain("DROP TYPE", script.Text);
        Assert.Single(script.Warnings);
    }

    [Fact]
    public void Transaction_StopsEveryBatchAfterAFailure()
    {
        var left = Set(("t.sql", "CREATE TABLE dbo.T (Id INT NOT NULL);"));
        var script = Script(left, Set()).Text;

        Assert.Contains("SET XACT_ABORT ON", script);
        Assert.Contains("BEGIN TRANSACTION", script);
        Assert.Contains("IF @@ERROR <> 0 SET NOEXEC ON", script);
        Assert.Contains("COMMIT TRANSACTION", script);

        var bare = Script(left, Set(), new ScriptOptions { UseTransaction = false }).Text;
        Assert.DoesNotContain("BEGIN TRANSACTION", bare);
    }

    [Fact]
    public void SmoExport_OntoItsOwnProject_NeedsNothing()
    {
        // The defaults that make the two shapes compare equal also keep the script empty.
        const string smo = "CREATE TABLE [dbo].[T]([Id] [int] IDENTITY(1,1) NOT NULL, [Fee] [money] NULL,\n CONSTRAINT [PK_T] PRIMARY KEY CLUSTERED ([Id] ASC) ON [PRIMARY]) ON [PRIMARY]\nGO\n"
            + "ALTER TABLE [dbo].[T] ADD  DEFAULT ((0)) FOR [Fee]\nGO\n";
        const string project = "CREATE TABLE [dbo].[T] ([Id] INT IDENTITY (1, 1) NOT NULL, [Fee] MONEY DEFAULT ((0)) NULL, CONSTRAINT [PK_T] PRIMARY KEY CLUSTERED ([Id] ASC));";
        Assert.Contains("Nothing to change", Script(Set(("Tables\\dbo.T.sql", smo)), Set(("dbo\\Tables\\T.sql", project))).Text);
    }
}
