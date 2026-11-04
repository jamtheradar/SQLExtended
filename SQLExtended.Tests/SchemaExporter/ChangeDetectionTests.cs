using System;
using System.IO;
using SQLExtended.SchemaExporter;
using Xunit;

namespace SQLExtended.Tests.SchemaExporter;

/// <summary>
/// Change detection decides when SchemaExporter may skip a database. A wrong skip leaves a schema change out of the
/// history with nothing to show for it; a wrong export only costs time. So every doubtful case must export.
/// </summary>
public sealed class ChangeDetectionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ChangeDetectionTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static DatabaseState Previous(string fingerprint = "abc", double daysAgo = 1) => new() { Fingerprint = fingerprint, LastFullScanUtc = Now.AddDays(-daysAgo) };

    [Fact]
    public void Skips_WhenFingerprintMatches_FolderExists_AndFullScanNotDue()
        => Assert.NotNull(ChangeDetection.SkipReason(Previous(), "abc", Now, fullScanEveryDays: 7, folderExists: true));

    [Fact]
    public void Exports_WhenFingerprintDiffers()
        => Assert.Null(ChangeDetection.SkipReason(Previous("abc"), "abd", Now, 7, true));

    [Fact]
    public void Exports_WhenFingerprintDiffersOnlyInCase()
        => Assert.Null(ChangeDetection.SkipReason(Previous("abc"), "ABC", Now, 7, true));

    [Fact]
    public void Exports_WhenNoPreviousState()
        => Assert.Null(ChangeDetection.SkipReason(null, "abc", Now, 7, true));

    [Fact]
    public void Exports_WhenTonightsFingerprintCouldNotBeRead()
        => Assert.Null(ChangeDetection.SkipReason(Previous(), null, Now, 7, true));

    [Fact]
    public void Exports_WhenTheDatabaseFolderIsMissingFromTheHistory()
        => Assert.Null(ChangeDetection.SkipReason(Previous(), "abc", Now, 7, folderExists: false));

    [Fact]
    public void Exports_WhenFullScanIsDue()
    {
        Assert.Null(ChangeDetection.SkipReason(Previous(daysAgo: 7), "abc", Now, 7, true));
        Assert.Null(ChangeDetection.SkipReason(Previous(daysAgo: 30), "abc", Now, 7, true));
        Assert.NotNull(ChangeDetection.SkipReason(Previous(daysAgo: 6.9), "abc", Now, 7, true));
    }

    [Fact]
    public void FullScanZero_MeansNeverForced()
        => Assert.NotNull(ChangeDetection.SkipReason(Previous(daysAgo: 365), "abc", Now, fullScanEveryDays: 0, folderExists: true));

    [Fact]
    public void Exports_WhenLastExportIsInTheFuture()
        => Assert.Null(ChangeDetection.SkipReason(Previous(daysAgo: -1), "abc", Now, 7, true));

    [Fact]
    public void Clean_WhenNothingFailed()
        => Assert.True(ChangeDetection.IsClean(false, 0, 0, 0, 0, decrypting: false));

    [Fact]
    public void NotClean_WhenDatabaseFailed_OrAFolderWasUnread_OrAFileWasUnwritten()
    {
        Assert.False(ChangeDetection.IsClean(databaseFailed: true, 0, 0, 0, 0, false));
        Assert.False(ChangeDetection.IsClean(false, unreadFolders: 1, 0, 0, 0, false));
        Assert.False(ChangeDetection.IsClean(false, 0, unwrittenFiles: 1, 0, 0, false));
    }

    [Fact]
    public void EncryptedModulesNotDecrypted_AreClean_OnlyWithDecryptionOff()
    {
        // Off: the same modules are missing every night, which is not a partial export.
        Assert.True(ChangeDetection.IsClean(false, 0, unwrittenFiles: 73, notDecryptedModules: 73, 0, decrypting: false));
        Assert.True(ChangeDetection.IsClean(false, 0, 0, 0, notDecryptedTriggers: 3, decrypting: false));
        // On: a DAC that failed tonight may work tomorrow, so the export must run again.
        Assert.False(ChangeDetection.IsClean(false, 0, 73, 73, 0, decrypting: true));
        Assert.False(ChangeDetection.IsClean(false, 0, 0, 0, 3, decrypting: true));
    }

    [Fact]
    public void NotClean_WhenSomethingBesidesEncryptedModulesWasUnwritten()
        => Assert.False(ChangeDetection.IsClean(false, 0, unwrittenFiles: 74, notDecryptedModules: 73, 0, decrypting: false));

    [Fact]
    public void State_RoundTrips_AndMatchesDatabaseNamesCaseInsensitively()
    {
        string path = Path.Combine(_root, "server.json");
        var state = new ServerState { OutputFolder = @"D:\History\SRV" };
        state.Databases["Sales"] = Previous();
        ChangeDetection.Save(path, state);
        ChangeDetection.Save(path, state);   // the second save replaces the existing file

        var loaded = ChangeDetection.Load(path, @"d:\history\srv");
        Assert.True(loaded.Databases.TryGetValue("SALES", out var db));
        Assert.Equal("abc", db.Fingerprint);
        Assert.Equal(Previous().LastFullScanUtc, db.LastFullScanUtc.ToUniversalTime());
    }

    [Fact]
    public void State_ForAnotherOutputFolder_IsIgnored()
    {
        string path = Path.Combine(_root, "server.json");
        var state = new ServerState { OutputFolder = @"D:\Old\SRV" };
        state.Databases["Sales"] = Previous();
        ChangeDetection.Save(path, state);

        Assert.Empty(ChangeDetection.Load(path, @"D:\New\SRV").Databases);
    }

    [Fact]
    public void State_Missing_OrCorrupt_IsEmpty()
    {
        Assert.Empty(ChangeDetection.Load(Path.Combine(_root, "missing.json"), "x").Databases);

        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "corrupt.json");
        File.WriteAllText(path, "{ not json");
        Assert.Empty(ChangeDetection.Load(path, "x").Databases);
    }
}
