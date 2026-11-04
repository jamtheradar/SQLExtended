using System;
using System.IO;
using System.Linq;
using SQLExtended.SchemaExporter;
using Xunit;

namespace SQLExtended.Tests.SchemaExporter;

/// <summary>
/// The mirror decides what SchemaExporter commits as dropped. Every failure it can make is a deletion in a git
/// history that nobody reads until they need it: a server that was offline for a night, a type folder the login
/// lost permission on, one object SMO choked on — each must read as "no change", never as "dropped".
/// </summary>
public sealed class SchemaMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SchemaMirrorTests", Guid.NewGuid().ToString("N"));
    private string Staging => Path.Combine(_root, "staging");
    private string Target => Path.Combine(_root, "target");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Put(string root, string relative, string text = "CREATE TABLE x (a int)")
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    private bool InTarget(string relative) => File.Exists(Path.Combine(Target, relative));

    private static MirrorScope Exported(params string[] databases)
    {
        var scope = new MirrorScope();
        foreach (string db in databases) scope.ExportedDatabases.Add(db);
        return scope;
    }

    [Fact]
    public void AddsChangesAndRemovesWithinAnExportedDatabase()
    {
        Put(Target, @"Sales\Tables\dbo.Kept.sql", "same");
        Put(Target, @"Sales\Tables\dbo.Changed.sql", "old");
        Put(Target, @"Sales\Tables\dbo.Dropped.sql");
        Put(Staging, @"Sales\Tables\dbo.Kept.sql", "same");
        Put(Staging, @"Sales\Tables\dbo.Changed.sql", "new");
        Put(Staging, @"Sales\Views\dbo.New.sql");

        var result = SchemaMirror.Apply(Staging, Target, Exported("Sales"));

        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Deleted));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Target, @"Sales\Tables\dbo.Changed.sql")));
        Assert.False(InTarget(@"Sales\Tables\dbo.Dropped.sql"));
        Assert.True(InTarget(@"Sales\Views\dbo.New.sql"));
    }

    [Fact]
    public void UnchangedExportChangesNothing()
    {
        Put(Target, @"Sales\Tables\dbo.A.sql", "same");
        Put(Staging, @"Sales\Tables\dbo.A.sql", "same");
        DateTime before = File.GetLastWriteTimeUtc(Path.Combine(Target, @"Sales\Tables\dbo.A.sql"));

        var result = SchemaMirror.Apply(Staging, Target, Exported("Sales"));

        Assert.False(result.Changed);
        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(Target, @"Sales\Tables\dbo.A.sql")));
    }

    [Fact]
    public void DatabaseOutsideTheScopeIsUntouched()
    {
        // Offline tonight, failed outright, or excluded: the export has nothing for it, which proves nothing.
        Put(Target, @"Offline\Tables\dbo.A.sql");

        var result = SchemaMirror.Apply(Staging, Target, Exported());

        Assert.False(result.Changed);
        Assert.True(InTarget(@"Offline\Tables\dbo.A.sql"));
    }

    [Fact]
    public void ExportedDatabaseWithNothingStagedRemovesItsScripts()
    {
        // A database that exported cleanly and has no objects left is a real, if unusual, change.
        Put(Target, @"Sales\Tables\dbo.A.sql");

        var result = SchemaMirror.Apply(Staging, Target, Exported("Sales"));

        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(Path.Combine(Target, "Sales")));
    }

    [Fact]
    public void UnreadTypeFolderKeepsItsScripts()
    {
        Put(Target, @"Sales\Stored Procedures\dbo.usp_A.sql");
        Put(Target, @"Sales\Tables\dbo.Gone.sql");
        var scope = Exported("Sales");
        scope.UnreadFolders.Add(@"Sales\Stored Procedures");

        SchemaMirror.Apply(Staging, Target, scope);

        Assert.True(InTarget(@"Sales\Stored Procedures\dbo.usp_A.sql"));
        Assert.False(InTarget(@"Sales\Tables\dbo.Gone.sql"));
    }

    [Fact]
    public void UnwrittenObjectKeepsItsPreviousScript()
    {
        // An encrypted module the DAC couldn't reach tonight, or one SMO threw on.
        Put(Target, @"Sales\Stored Procedures\dbo.usp_Encrypted.sql", "last good");
        Put(Staging, @"Sales\Stored Procedures\dbo.usp_Other.sql");
        var scope = Exported("Sales");
        scope.UnwrittenFiles.Add(@"Sales\Stored Procedures\dbo.usp_Encrypted.sql");

        var result = SchemaMirror.Apply(Staging, Target, scope);

        Assert.Equal(1, result.Kept);
        Assert.Equal(0, result.Deleted);
        Assert.Equal("last good", File.ReadAllText(Path.Combine(Target, @"Sales\Stored Procedures\dbo.usp_Encrypted.sql")));
    }

    [Fact]
    public void DroppedDatabaseLosesOnlyItsScripts()
    {
        Put(Target, @"Old\Tables\dbo.A.sql");
        Put(Target, @"Old\Tables\notes.txt", "mine");
        Put(Target, @"Old\Tables\Old.sqlproj", "<Project />");
        Put(Target, @"Old\Docs\readme.sql", "not a type folder");
        var scope = new MirrorScope();
        scope.DroppedDatabases.Add("Old");

        var result = SchemaMirror.Apply(Staging, Target, scope);

        Assert.Equal(1, result.Deleted);
        Assert.True(InTarget(@"Old\Tables\notes.txt"));
        Assert.True(InTarget(@"Old\Tables\Old.sqlproj"));
        Assert.True(InTarget(@"Old\Docs\readme.sql"));
    }

    [Fact]
    public void ExportedWinsOverDropped()
    {
        Put(Target, @"Sales\Tables\dbo.A.sql");
        Put(Staging, @"Sales\Tables\dbo.A.sql");
        var scope = Exported("Sales");
        scope.DroppedDatabases.Add("Sales");

        var result = SchemaMirror.Apply(Staging, Target, scope);

        Assert.False(result.Changed);
        Assert.True(InTarget(@"Sales\Tables\dbo.A.sql"));
    }

    [Fact]
    public void CaseOnlyRenameIsWrittenUnderTheNewName()
    {
        Put(Target, @"Sales\Tables\dbo.Foo.sql", "same");
        Put(Staging, @"Sales\Tables\dbo.foo.sql", "same");

        var result = SchemaMirror.Apply(Staging, Target, Exported("Sales"));

        Assert.Equal(1, result.Updated);
        Assert.Equal("dbo.foo.sql", Path.GetFileName(Directory.GetFiles(Path.Combine(Target, @"Sales\Tables")).Single()));
    }

    [Fact]
    public void ExistingDatabaseFoldersAreThoseHoldingATypeFolder()
    {
        Put(Target, @"Sales\Tables\dbo.A.sql");
        Put(Target, @"Ops\Views\dbo.V.sql");
        Put(Target, @"docs\readme.md", "x");
        Directory.CreateDirectory(Path.Combine(Target, ".git", "objects"));

        var folders = SchemaMirror.ExistingDatabaseFolders(Target).OrderBy(f => f).ToList();

        Assert.Equal(new[] { "Ops", "Sales" }, folders);
    }
}
