using System;
using System.Collections.Generic;
using System.Linq;
using SQLExtended.ScriptCast;
using Xunit;

namespace SQLExtended.Tests.ScriptCast;

/// <summary>
/// Each of these fails quietly on screen: a GO inside a comment splits a procedure in two and the server reports a
/// syntax error that reads like the script's fault; a Cast List entry dropped without a word means a database never
/// gets the change; a result set merged by position puts one database's column under another's header.
/// </summary>
public class BatchSplitterTests
{
    [Fact]
    public void SplitsOnGoLines_AnyCase_WithRepeatAndComment()
    {
        var batches = BatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2\r\ngo 3\r\nSELECT 3\r\n  Go  -- done\r\n");

        Assert.Equal(new[] { "SELECT 1", "SELECT 2", "SELECT 3" }, batches.Select(b => b.Text));
        Assert.Equal(new[] { 1, 3, 1 }, batches.Select(b => b.RepeatCount));
        Assert.Equal(new[] { 1, 3, 5 }, batches.Select(b => b.StartLine));
    }

    [Fact]
    public void GoInsideBlockComment_IsText_EvenNested()
    {
        var batches = BatchSplitter.Split("SELECT 1\n/* outer /* inner */\nGO\n*/\nSELECT 2\nGO\nSELECT 3");

        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 2", batches[0].Text);
        Assert.Equal("SELECT 3", batches[1].Text);
    }

    [Fact]
    public void GoInsideMultiLineString_IsText()
    {
        var batches = BatchSplitter.Split("PRINT 'line one\nGO\nline three'\nGO\nSELECT 1");

        Assert.Equal(2, batches.Count);
        Assert.Contains("GO", batches[0].Text);
    }

    [Fact]
    public void LineCommentWithQuote_DoesNotOpenAString()
    {
        var batches = BatchSplitter.Split("SELECT 1 -- don't\nGO\nSELECT 2");
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void EscapedQuotesAndBrackets_StayBalanced()
    {
        var batches = BatchSplitter.Split("SELECT 'it''s', [a]]b]\nGO\nSELECT 2");
        Assert.Equal(2, batches.Count);
    }

    [Theory]
    [InlineData("GOTO label")]
    [InlineData("SELECT 1 GO")]
    [InlineData("GO;")]
    public void NotASeparator(string line)
    {
        Assert.Single(BatchSplitter.Split("SELECT 1\n" + line + "\nSELECT 2"));
    }

    [Fact]
    public void EmptyBatches_AreDropped_AndLeadingBlankLinesMoveStartLine()
    {
        var batches = BatchSplitter.Split("GO\n\nGO\n\n\nSELECT 1\nGO\n   \n");

        Assert.Single(batches);
        Assert.Equal(6, batches[0].StartLine);
        Assert.Equal("SELECT 1", batches[0].Text);
    }
}

public class CastListTests
{
    [Fact]
    public void ParsesIssueFormat_RoundTrips()
    {
        const string json = @"{
          ""name"": ""Production - EU"",
          ""description"": ""All EU customer databases"",
          ""servers"": [
            { ""server"": ""sql-eu-01.contoso.local"", ""databases"": [""Customer_DE"", ""Customer_FR""] },
            { ""server"": ""sql-eu-02.contoso.local"", ""databases"": [""Customer_NL""] }
          ]
        }";

        var list = CastListSerializer.Parse(json);

        Assert.Equal("Production - EU", list.Name);
        Assert.Equal("All EU customer databases", list.Description);
        Assert.Equal(2, list.Servers.Count);
        Assert.Equal(3, list.DatabaseCount);
        Assert.Empty(list.Issues);

        var again = CastListSerializer.Parse(CastListSerializer.Serialize(list));
        Assert.Equal(list.Name, again.Name);
        Assert.Equal(list.Servers.SelectMany(s => s.Databases), again.Servers.SelectMany(s => s.Databases));
    }

    [Fact]
    public void BadEntries_AreIssues_AndTheRestLoads()
    {
        const string json = @"{ ""servers"": [
            { ""server"": ""a"", ""databases"": [""One"", 7, """", ""one"", ""Two""] },
            { ""databases"": [""X""] },
            42,
            { ""server"": ""b"", ""databases"": ""Three"" },
            { ""server"": ""A"", ""databases"": [""Four""] },
            { ""server"": ""c"" }
        ] }";

        var list = CastListSerializer.Parse(json, "fallback");

        Assert.Equal("fallback", list.Name);
        Assert.Equal(new[] { "a", "b", "c" }, list.Servers.Select(s => s.Server));
        Assert.Equal(new[] { "One", "Two", "Four" }, list.Servers[0].Databases);   // duplicate server merged, case-insensitive
        Assert.Equal(8, list.Issues.Count);
        Assert.Contains(list.Issues, i => i.Contains("databases[1]"));
        Assert.Contains(list.Issues, i => i.Contains("\"one\" listed more than once"));
        Assert.Contains(list.Issues, i => i.Contains("no \"server\""));
        Assert.Contains(list.Issues, i => i.Contains("merged"));
    }

    [Fact]
    public void CredentialKeys_AreIgnored_WithAnIssue_AndNeverWritten()
    {
        var list = CastListSerializer.Parse(@"{ ""servers"": [ { ""server"": ""a"", ""password"": ""hunter2"", ""User Id"": ""sa"", ""databases"": [""X""] } ] }");

        Assert.Equal(2, list.Issues.Count);
        Assert.All(list.Issues, i => Assert.Contains("never carry credentials", i));
        string written = CastListSerializer.Serialize(list);
        Assert.DoesNotContain("hunter2", written);
        Assert.DoesNotContain("sa\"", written);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{ \"name\": \"x\" }")]
    [InlineData("{ \"servers\": {} }")]
    public void NotACastList_Throws(string json)
    {
        Assert.Throws<CastListFormatException>(() => CastListSerializer.Parse(json));
    }

    [Theory]
    [InlineData("SQL-EU-01", "sql-eu-01")]
    [InlineData("tcp:sql-eu-01,1433", "SQL-EU-01")]
    [InlineData(".", "(local)")]
    [InlineData("localhost\\SQL2022", ".\\sql2022")]
    [InlineData("127.0.0.1", "(local)")]
    public void ServerNames_ThatAreTheSameServer_Match(string a, string b) => Assert.True(CastServerName.Same(a, b));

    [Theory]
    [InlineData("sql-eu-01", "sql-eu-02")]
    [InlineData("sql-eu-01,1444", "sql-eu-01")]
    [InlineData(".\\A", ".\\B")]
    public void DifferentServers_DoNotMatch(string a, string b) => Assert.False(CastServerName.Same(a, b));
}

public class ScriptCastAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Customers")]
    [InlineData("DECLARE @x int = 1; SET @x = 2; PRINT @x; SELECT @x")]
    [InlineData("SELECT * INTO #t FROM dbo.Customers; SELECT * FROM #t; DROP TABLE #t")]
    [InlineData("CREATE TABLE #t (Id int); INSERT INTO #t VALUES (1); UPDATE #t SET Id = 2; DELETE FROM #t")]
    [InlineData("DECLARE @t TABLE (Id int); INSERT INTO @t VALUES (1); DELETE FROM @t")]
    [InlineData("IF 1 = 1 BEGIN SELECT 1 END ELSE BEGIN SELECT 2 END")]
    [InlineData("SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT 1")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    public void ReadOnlyScripts_DoNotNeedConfirming(string sql)
    {
        var report = ScriptCastAnalyzer.Analyze(sql);
        Assert.False(report.MayChange, string.Join(", ", report.Kinds));
    }

    [Theory]
    [InlineData("UPDATE dbo.Customers SET Active = 0 WHERE Id = 1", "UPDATE")]
    [InlineData("INSERT INTO dbo.Log (Msg) VALUES ('x')", "INSERT")]
    [InlineData("DELETE FROM dbo.Log WHERE Id < 10", "DELETE")]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.X = s.X;", "MERGE")]
    [InlineData("TRUNCATE TABLE dbo.Log", "TRUNCATE TABLE")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Customers", "SELECT INTO")]
    [InlineData("CREATE TABLE dbo.New (Id int)", "CREATE TABLE")]
    [InlineData("DROP TABLE dbo.Old", "DROP TABLE")]
    [InlineData("DROP TABLE #t, dbo.Old", "DROP TABLE")]
    [InlineData("ALTER TABLE dbo.T ADD X int", "ALTER TABLE")]
    [InlineData("CREATE INDEX IX ON dbo.T (X)", "CREATE INDEX")]
    [InlineData("EXEC dbo.DoThings", "EXEC")]
    [InlineData("GRANT SELECT ON dbo.T TO SomeRole", "GRANT")]
    [InlineData("IF 1 = 1 BEGIN UPDATE dbo.T SET X = 1 END", "UPDATE")]
    public void ChangingScripts_AreReported(string sql, string kind)
    {
        var report = ScriptCastAnalyzer.Analyze(sql);
        Assert.True(report.MayChange);
        Assert.Contains(kind, report.Kinds);
    }

    [Fact]
    public void CreateProcedure_IsReported_InEveryBatch()
    {
        var report = ScriptCastAnalyzer.Analyze("SELECT 1\nGO\nCREATE PROCEDURE dbo.P AS SELECT 1\nGO\nALTER VIEW dbo.V AS SELECT 1 AS x");
        Assert.Contains("CREATE PROCEDURE", report.Kinds);
        Assert.Contains("ALTER VIEW", report.Kinds);
    }

    [Fact]
    public void UnparseableScript_CountsAsChanging()
    {
        var report = ScriptCastAnalyzer.Analyze(":setvar Db Prod\nSELECT * FROM $(Db).dbo.T");
        Assert.True(report.ParseFailed);
        Assert.True(report.MayChange);
    }

    [Theory]
    [InlineData("UPDATE dbo.T SET X = 1 WHERE Id = 1")]
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT 1\nGO\nEXEC dbo.P")]
    [InlineData("SAVE TRANSACTION Here; UPDATE dbo.T SET X = 1 WHERE Id = 1")]
    [InlineData("ALTER TABLE dbo.T ADD X int")]
    [InlineData("")]
    public void TransactionMode_AllowsOrdinaryScripts(string sql)
        => Assert.Empty(ScriptCastAnalyzer.TransactionBlockers(sql));

    [Theory]
    [InlineData("BEGIN TRAN; UPDATE dbo.T SET X = 1 WHERE Id = 1; COMMIT", "BEGIN TRANSACTION")]
    [InlineData("UPDATE dbo.T SET X = 1 WHERE Id = 1\nGO\nCOMMIT", "COMMIT")]
    [InlineData("IF @@ERROR <> 0 ROLLBACK", "ROLLBACK")]
    [InlineData("BEGIN TRY UPDATE dbo.T SET X = 1 WHERE Id = 1 END TRY BEGIN CATCH ROLLBACK TRANSACTION END CATCH", "ROLLBACK")]
    [InlineData("BACKUP DATABASE Db TO DISK = 'x.bak'", "BACKUP")]
    [InlineData("RESTORE DATABASE Db FROM DISK = 'x.bak'", "RESTORE")]
    [InlineData("CREATE DATABASE NewDb", "CREATE DATABASE")]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE", "ALTER DATABASE")]
    [InlineData("DROP DATABASE OldDb", "DROP DATABASE")]
    [InlineData("CREATE FULLTEXT CATALOG Cat", "CREATE FULLTEXT")]
    [InlineData("RECONFIGURE", "RECONFIGURE")]
    public void TransactionMode_IsBlocked_ByOwnTransactionsAndUntransactableStatements(string sql, string expected)
    {
        var reasons = ScriptCastAnalyzer.TransactionBlockers(sql);
        Assert.Contains(reasons, r => r.StartsWith(expected));
    }

    [Fact]
    public void TransactionBlocker_NamesTheScriptLine_NotTheBatchLine()
    {
        var reasons = ScriptCastAnalyzer.TransactionBlockers("SELECT 1\nGO\nSELECT 2\n\nCOMMIT");
        Assert.Equal("COMMIT: the script would commit the cast's transaction (line 5)", Assert.Single(reasons));
    }

    [Fact]
    public void TransactionMode_IsBlocked_WhenTheScriptDoesNotParse()
        => Assert.Single(ScriptCastAnalyzer.TransactionBlockers(":setvar Db Prod\nSELECT 1"));
}

public class ResultMergerTests
{
    private static ResultSetCapture Set(int ordinal, string columns, params object[][] rows)
    {
        var capture = new ResultSetCapture(ordinal, columns.Split(',').Select(c => new ResultColumn(c, typeof(int), "int")).ToList());
        capture.Rows.AddRange(rows);
        return capture;
    }

    private static DatabaseResults Db(string server, string db, params ResultSetCapture[] sets) => new(server, db, sets);

    [Fact]
    public void SameShape_MergesInTargetOrder_PerResultSet()
    {
        var merged = ResultMerger.Merge(new[]
        {
            Db("s1", "A", Set(0, "Id", new object[] { 1 }), Set(1, "X,Y", new object[] { 9, 9 })),
            Db("s1", "B", Set(0, "Id", new object[] { 2 }, new object[] { 3 }), Set(1, "X,Y", new object[] { 8, 8 })),
            Db("s2", "A", Set(0, "Id", new object[] { 4 })),
        });

        Assert.Equal(2, merged.Count);
        Assert.Equal(0, merged[0].Ordinal);
        Assert.Equal(new[] { "A", "B", "B", "A" }, merged[0].Rows.Select(r => r.Database));
        Assert.Equal(new object[] { 1, 2, 3, 4 }, merged[0].Rows.Select(r => r.Values[0]));
        Assert.Equal(new[] { "s1 / A", "s1 / B" }, merged[1].Sources);
        Assert.All(merged, m => Assert.False(m.Mismatched));
    }

    [Fact]
    public void DifferentColumns_AreKeptApart_AndFlagged_MajorityIsMain()
    {
        var merged = ResultMerger.Merge(new[]
        {
            Db("s", "Old", Set(0, "Id,Name", new object[] { 1, 2 })),
            Db("s", "New1", Set(0, "Id,Name,Email", new object[] { 1, 2, 3 })),
            Db("s", "New2", Set(0, "Id,Name,Email", new object[] { 4, 5, 6 })),
        });

        Assert.Equal(2, merged.Count);
        Assert.False(merged[0].Mismatched);
        Assert.Equal(3, merged[0].Columns.Count);
        Assert.Equal(new[] { "s / New1", "s / New2" }, merged[0].Sources);
        Assert.True(merged[1].Mismatched);
        Assert.Equal(new[] { "s / Old" }, merged[1].Sources);
    }

    [Fact]
    public void SameNamesDifferentTypes_AreDifferentShapes()
    {
        var a = new ResultSetCapture(0, new List<ResultColumn> { new("Id", typeof(int), "int") });
        var b = new ResultSetCapture(0, new List<ResultColumn> { new("Id", typeof(string), "varchar") });
        var merged = ResultMerger.Merge(new[] { Db("s", "A", a), Db("s", "B", b) });

        Assert.Equal(2, merged.Count);
        Assert.Equal("s / A", merged[0].Sources.Single());   // tie: the first listed is the main grid
    }

    [Fact]
    public void ColumnNameCase_DoesNotSplitAGrid()
    {
        var merged = ResultMerger.Merge(new[] { Db("s", "A", Set(0, "id")), Db("s", "B", Set(0, "ID")) });
        Assert.Single(merged);
    }

    [Fact]
    public void FailedDatabase_WithNoResults_IsSimplyAbsent()
    {
        var merged = ResultMerger.Merge(new[] { Db("s", "A"), Db("s", "B", Set(0, "Id", new object[] { 1 })) });
        Assert.Single(merged);
        Assert.Equal("s / B", merged[0].Sources.Single());
    }
}
