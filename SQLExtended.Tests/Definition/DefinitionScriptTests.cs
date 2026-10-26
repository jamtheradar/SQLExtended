using SQLExtended.Definition;
using Xunit;

namespace SQLExtended.Tests.Definition;

/// <summary>
/// Pins which CREATE Open Definition rewrites. A module's stored text often opens with a header comment that itself says
/// "create procedure", and rewriting that instead of the statement leaves a script that still fails on F5.
/// </summary>
public class DefinitionScriptTests
{
    [Theory]
    [InlineData(DefinitionScriptMode.Create, "CREATE PROCEDURE dbo.p AS SELECT 1")]
    [InlineData(DefinitionScriptMode.Alter, "ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData(DefinitionScriptMode.CreateOrAlter, "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1")]
    public void RewritesTheModuleStatement(DefinitionScriptMode mode, string expected)
    {
        Assert.Equal(expected, DefinitionScript.ApplyMode("CREATE PROCEDURE dbo.p AS SELECT 1", mode));
    }

    [Theory]
    [InlineData("create proc dbo.p as select 1", "ALTER proc dbo.p as select 1")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS x", "ALTER VIEW dbo.v AS SELECT 1 AS x")]
    [InlineData("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE TRIGGER dbo.t ON dbo.x AFTER INSERT AS SELECT 1", "ALTER TRIGGER dbo.t ON dbo.x AFTER INSERT AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", "ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE\r\n\tPROCEDURE dbo.p AS SELECT 1", "ALTER\r\n\tPROCEDURE dbo.p AS SELECT 1")]
    public void EveryModuleKind_AndAnyCaseOrSpacing(string script, string expected)
    {
        Assert.Equal(expected, DefinitionScript.ApplyMode(script, DefinitionScriptMode.Alter));
    }

    [Fact]
    public void CommentsAndStringsAreSkipped()
    {
        const string script =
            "-- Procedure: [dbo].[p]\r\n" +
            "/* History: create procedure dbo.p  /* nested create view */ still comment */\r\n" +
            "CREATE PROCEDURE dbo.p AS SELECT 'CREATE VIEW x'";

        string result = DefinitionScript.ApplyMode(script, DefinitionScriptMode.CreateOrAlter);

        Assert.Contains("/* History: create procedure dbo.p  /* nested create view */ still comment */", result);
        Assert.EndsWith("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 'CREATE VIEW x'", result);
    }

    [Theory]
    [InlineData("CREATE TABLE [dbo].[t] ( id int )")]
    [InlineData("-- encrypted: definition unavailable")]
    [InlineData("")]
    public void AnythingElse_IsUnchanged(string script)
    {
        Assert.Equal(script, DefinitionScript.ApplyMode(script, DefinitionScriptMode.Alter));
    }

    [Fact]
    public void Build_PutsAUseAhead_AndQuotesTheDatabase()
    {
        Assert.Equal("USE [Sales]]Db]\r\nGO\r\n\r\nALTER VIEW v AS SELECT 1", DefinitionScript.Build("Sales]Db", "CREATE VIEW v AS SELECT 1", DefinitionScriptMode.Alter));
    }

    [Fact]
    public void Build_WithoutAMode_LeavesTheScriptAlone()
    {
        Assert.Equal("USE [d]\r\nGO\r\n\r\nCREATE OR ALTER VIEW v AS SELECT 1", DefinitionScript.Build("d", "CREATE OR ALTER VIEW v AS SELECT 1", null));
    }

    [Fact]
    public void Build_WithoutADatabase_HasNoUse()
    {
        Assert.Equal("CREATE VIEW v AS SELECT 1", DefinitionScript.Build(null, "CREATE VIEW v AS SELECT 1", DefinitionScriptMode.Create));
    }
}
