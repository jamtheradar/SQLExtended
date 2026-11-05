using System;
using System.Collections.Generic;
using System.IO;
using SQLExtended.Theme;
using Xunit;

namespace SQLExtended.Tests.Theme;

/// <summary>The file SSMS leaves for SchemaCompare: whatever the extension resolves, the app must read back unchanged.</summary>
public class ThemeSnapshotTests
{
    [Fact]
    public void ResolvedPalette_RoundTrips()
    {
        var hex = ThemePalette.Resolve(dark: true, new Dictionary<string, string> { ["SqlxSurface"] = "#2D2D30", ["SqlxOverlay"] = "#33000000" });

        Assert.True(ThemeSnapshot.TryParse(ThemeSnapshot.Format(true, hex), out bool dark, out var read));

        Assert.True(dark);
        Assert.Equal(hex, read);
    }

    [Fact]
    public void LightFlag_AndJunkLines()
    {
        Assert.True(ThemeSnapshot.TryParse("# comment\r\ndark=false\r\nSqlxText=#1E1E1E\r\nnot a line\r\nSqlxBad=red\r\n", out bool dark, out var hex));

        Assert.False(dark);
        Assert.Equal(new Dictionary<string, string> { ["SqlxText"] = "#1E1E1E" }, hex);
    }

    [Fact]
    public void NoDarkLine_IsNotASnapshot()
    {
        Assert.False(ThemeSnapshot.TryParse("SqlxText=#1E1E1E", out _, out _));
    }

    [Fact]
    public void Write_ReplacesTheOldFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "ThemeSnapshotTests_" + Guid.NewGuid().ToString("N"), ThemeSnapshot.FileName);
        try
        {
            ThemeSnapshot.Write(path, true, new Dictionary<string, string> { ["SqlxSurface"] = "#1E1E1E" });
            ThemeSnapshot.Write(path, false, new Dictionary<string, string> { ["SqlxSurface"] = "#FFFFFF" });

            Assert.True(ThemeSnapshot.TryRead(path, out bool dark, out var hex));
            Assert.False(dark);
            Assert.Equal("#FFFFFF", hex["SqlxSurface"]);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path), recursive: true);
        }
    }
}
